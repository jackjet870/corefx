// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.NetworkInformation
{
    public partial class Ping : IDisposable
    {
        private const int DefaultSendBufferSize = 32;  // Same as ping.exe on Windows.
        private const int DefaultTimeout = 5000;       // 5 seconds: same as ping.exe on Windows.
        private const int MaxBufferSize = 65500;       // Artificial constraint due to win32 api limitations.
        private const int MaxUdpPacket = 0xFFFF + 256; // Marshal.SizeOf(typeof(Icmp6EchoReply)) * 2 + ip header info;

        private readonly ManualResetEventSlim _lockObject = new ManualResetEventSlim(initialState: true); // doubles as the ability to wait on the current operation
        private SendOrPostCallback _onPingCompletedDelegate;
        private bool _disposeRequested = false;
        private byte[] _defaultSendBuffer = null;
        private bool _canceled;

        // Thread safety:
        private const int Free = 0;
        private const int InProgress = 1;
        private const int Disposed = 2;
        private int _status = Free;

        public Ping()
        {
            // This class once inherited a finalizer. For backward compatibility it has one so that 
            // any derived class that depends on it will see the behaviour expected. Since it is
            // not used by this class itself, suppress it immediately if this is not an instance
            // of a derived class it doesn't suffer the GC burden of finalization.
            if (GetType() == typeof(Ping))
            {
                GC.SuppressFinalize(this);
            }
        }

        ~Ping()
        {
            Dispose(false);
        }

        private void CheckStart()
        {
            if (_disposeRequested)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }

            int currentStatus;
            lock (_lockObject)
            {
                currentStatus = _status;
                if (currentStatus == Free)
                {
                    _canceled = false;
                    _status = InProgress;
                    _lockObject.Reset();
                    return;
                }
            }

            if (currentStatus == InProgress)
            {
                throw new InvalidOperationException(SR.net_inasync);
            }
            else
            {
                Debug.Assert(currentStatus == Disposed, $"Expected currentStatus == Disposed, got {currentStatus}");
                throw new ObjectDisposedException(GetType().FullName);
            }
        }

        private void Finish()
        {
            lock (_lockObject)
            {
                Debug.Assert(_status == InProgress, $"Invalid status: {_status}");
                _status = Free;
                _lockObject.Set();
            }

            if (_disposeRequested)
            {
                InternalDispose();
            }
        }

        // Cancels pending async requests, closes the handles.
        private void InternalDispose()
        {
            _disposeRequested = true;

            lock (_lockObject)
            {
                if (_status != Free)
                {
                    // Already disposed, or Finish will call Dispose again once Free.
                    return;
                }
                _status = Disposed;
            }

            InternalDisposeCore();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Only on explicit dispose.  Otherwise, the GC can cleanup everything else.
                InternalDispose();
            }
        }

        public event PingCompletedEventHandler PingCompleted;

        protected void OnPingCompleted(PingCompletedEventArgs e)
        {
            PingCompleted?.Invoke(this, e);
        }

        public PingReply Send(string hostNameOrAddress)
        {
            return Send(hostNameOrAddress, DefaultTimeout, DefaultSendBuffer);
        }

        public PingReply Send(string hostNameOrAddress, int timeout)
        {
            return Send(hostNameOrAddress, timeout, DefaultSendBuffer);
        }

        public PingReply Send(IPAddress address)
        {
            return Send(address, DefaultTimeout, DefaultSendBuffer);
        }

        public PingReply Send(IPAddress address, int timeout)
        {
            return Send(address, timeout, DefaultSendBuffer);
        }

        public PingReply Send(string hostNameOrAddress, int timeout, byte[] buffer)
        {
            return Send(hostNameOrAddress, timeout, buffer, null);
        }

        public PingReply Send(IPAddress address, int timeout, byte[] buffer)
        {
            return Send(address, timeout, buffer, null);
        }

        public PingReply Send(string hostNameOrAddress, int timeout, byte[] buffer, PingOptions options)
        {
            return SendPingAsync(hostNameOrAddress, timeout, buffer, options).GetAwaiter().GetResult();
        }

        public PingReply Send(IPAddress address, int timeout, byte[] buffer, PingOptions options)
        {
            return SendPingAsync(address, timeout, buffer, options).GetAwaiter().GetResult();
        }

        public void SendAsync(string hostNameOrAddress, object userToken)
        {
            SendAsync(hostNameOrAddress, DefaultTimeout, DefaultSendBuffer, userToken);
        }

        public void SendAsync(string hostNameOrAddress, int timeout, object userToken)
        {
            SendAsync(hostNameOrAddress, timeout, DefaultSendBuffer, userToken);
        }

        public void SendAsync(IPAddress address, object userToken)
        {
            SendAsync(address, DefaultTimeout, DefaultSendBuffer, userToken);
        }

        public void SendAsync(IPAddress address, int timeout, object userToken)
        {
            SendAsync(address, timeout, DefaultSendBuffer, userToken);
        }

        public void SendAsync(string hostNameOrAddress, int timeout, byte[] buffer, object userToken)
        {
            SendAsync(hostNameOrAddress, timeout, buffer, null, userToken);
        }

        public void SendAsync(IPAddress address, int timeout, byte[] buffer, object userToken)
        {
            SendAsync(address, timeout, buffer, null, userToken);
        }

        public void SendAsync(string hostNameOrAddress, int timeout, byte[] buffer, PingOptions options, object userToken)
        {
            TranslateTaskToEap(userToken, SendPingAsync(hostNameOrAddress, timeout, buffer, options));
        }

        public void SendAsync(IPAddress address, int timeout, byte[] buffer, PingOptions options, object userToken)
        {
            TranslateTaskToEap(userToken, SendPingAsync(address, timeout, buffer, options));
        }

        private void TranslateTaskToEap(object userToken, Task<PingReply> pingTask)
        {
            pingTask.ContinueWith((t, state) =>
            {
                var asyncOp = (AsyncOperation)state;
                var e = new PingCompletedEventArgs(t.Status == TaskStatus.RanToCompletion ? t.Result : null, t.Exception, t.IsCanceled, asyncOp.UserSuppliedState);
                SendOrPostCallback callback = _onPingCompletedDelegate ?? (_onPingCompletedDelegate = new SendOrPostCallback(o => { OnPingCompleted((PingCompletedEventArgs)o); }));
                asyncOp.PostOperationCompleted(callback, e);
            }, AsyncOperationManager.CreateOperation(userToken), CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        public Task<PingReply> SendPingAsync(IPAddress address)
        {
            return SendPingAsync(address, DefaultTimeout, DefaultSendBuffer, null);
        }

        public Task<PingReply> SendPingAsync(string hostNameOrAddress)
        {
            return SendPingAsync(hostNameOrAddress, DefaultTimeout, DefaultSendBuffer, null);
        }

        public Task<PingReply> SendPingAsync(IPAddress address, int timeout)
        {
            return SendPingAsync(address, timeout, DefaultSendBuffer, null);
        }

        public Task<PingReply> SendPingAsync(string hostNameOrAddress, int timeout)
        {
            return SendPingAsync(hostNameOrAddress, timeout, DefaultSendBuffer, null);
        }

        public Task<PingReply> SendPingAsync(IPAddress address, int timeout, byte[] buffer)
        {
            return SendPingAsync(address, timeout, buffer, null);
        }

        public Task<PingReply> SendPingAsync(string hostNameOrAddress, int timeout, byte[] buffer)
        {
            return SendPingAsync(hostNameOrAddress, timeout, buffer, null);
        }

        public Task<PingReply> SendPingAsync(IPAddress address, int timeout, byte[] buffer, PingOptions options)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (buffer.Length > MaxBufferSize)
            {
                throw new ArgumentException(SR.net_invalidPingBufferSize, nameof(buffer));
            }

            if (timeout < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            if (address == null)
            {
                throw new ArgumentNullException(nameof(address));
            }

            // Check if address family is installed.
            TestIsIpSupported(address);

            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                throw new ArgumentException(SR.net_invalid_ip_addr, nameof(address));
            }

            // Need to snapshot the address here, so we're sure that it's not changed between now
            // and the operation, and to be sure that IPAddress.ToString() is called and not some override.
            IPAddress addressSnapshot = (address.AddressFamily == AddressFamily.InterNetwork) ?
                new IPAddress(address.GetAddressBytes()) :
                new IPAddress(address.GetAddressBytes(), address.ScopeId);

            CheckStart();
            try
            {
                return SendPingAsyncCore(addressSnapshot, buffer, timeout, options);
            }
            catch (Exception e)
            {
                Finish();
                return Task.FromException<PingReply>(new PingException(SR.net_ping, e));
            }
        }

        public Task<PingReply> SendPingAsync(string hostNameOrAddress, int timeout, byte[] buffer, PingOptions options)
        {
            if (string.IsNullOrEmpty(hostNameOrAddress))
            {
                throw new ArgumentNullException(nameof(hostNameOrAddress));
            }

            IPAddress address;
            if (IPAddress.TryParse(hostNameOrAddress, out address))
            {
                return SendPingAsync(address, timeout, buffer, options);
            }

            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (buffer.Length > MaxBufferSize)
            {
                throw new ArgumentException(SR.net_invalidPingBufferSize, nameof(buffer));
            }

            if (timeout < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            CheckStart();
            return GetAddressAndSendAsync(hostNameOrAddress, timeout, buffer, options);
        }

        public void SendAsyncCancel()
        {
            lock (_lockObject)
            {
                if (!_lockObject.IsSet)
                {
                    // As in the .NET Framework, this doesn't actually cancel an in-progress operation.  It just marks it such that
                    // when the operation completes, it's flagged as canceled.
                    _canceled = true;
                }
            }

            // As in the .NET Framework, synchronously wait for the in-flight operation to complete.
            // If there isn't one in flight, this event will already be set.
            _lockObject.Wait();
        }

        private async Task<PingReply> GetAddressAndSendAsync(string hostNameOrAddress, int timeout, byte[] buffer, PingOptions options)
        {
            bool requiresFinish = true;
            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(hostNameOrAddress).ConfigureAwait(false);
                Task<PingReply> pingReplyTask = SendPingAsyncCore(addresses[0], buffer, timeout, options);
                requiresFinish = false;
                return await pingReplyTask.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // SendPingAsyncCore will call Finish before completing the Task.  If SendPingAsyncCore isn't invoked
                // because an exception is thrown first, or if it throws out an exception synchronously, then
                // we need to invoke Finish; otherwise, it has the responsibility to invoke Finish.
                if (requiresFinish)
                {
                    Finish();
                }
                throw new PingException(SR.net_ping, e);
            }
        }

        // Tests if the current machine supports the given ip protocol family.
        private void TestIsIpSupported(IPAddress ip)
        {
            InitializeSockets();

            if (ip.AddressFamily == AddressFamily.InterNetwork && !SocketProtocolSupportPal.OSSupportsIPv4)
            {
                throw new NotSupportedException(SR.net_ipv4_not_installed);
            }
            else if ((ip.AddressFamily == AddressFamily.InterNetworkV6 && !SocketProtocolSupportPal.OSSupportsIPv6))
            {
                throw new NotSupportedException(SR.net_ipv6_not_installed);
            }
        }

        static partial void InitializeSockets();
        partial void InternalDisposeCore();

        // Creates a default send buffer if a buffer wasn't specified.  This follows the ping.exe model.
        private byte[] DefaultSendBuffer
        {
            get
            {
                if (_defaultSendBuffer == null)
                {
                    _defaultSendBuffer = new byte[DefaultSendBufferSize];
                    for (int i = 0; i < DefaultSendBufferSize; i++)
                        _defaultSendBuffer[i] = (byte)((int)'a' + i % 23);
                }
                return _defaultSendBuffer;
            }
        }
    }
}
