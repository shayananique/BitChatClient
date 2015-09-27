﻿/*
Technitium Bit Chat
Copyright (C) 2015  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using TechnitiumLibrary.Net;
using TechnitiumLibrary.Net.UPnP.Networking;

namespace BitChatClient.Network.Connections
{
    public enum InternetConnectivityStatus
    {
        Unknown = 0,
        NoInternetConnection = 1,
        DirectInternetConnection = 2,
        HttpProxyInternetConnection = 3,
        Socks5ProxyInternetConnection = 4,
        NatInternetConnectionViaUPnPRouter = 5,
        NatInternetConnection = 6
    }

    public enum UPnPDeviceStatus
    {
        Unknown = 0,
        DeviceNotFound = 1,
        ExternalIpPrivate = 2,
        PortForwarded = 3,
        PortForwardingFailed = 4,
        PortForwardedNotAccessible = 5
    }

    class ConnectionManager : IConnectionInfo, IDisposable
    {
        #region events

        public event EventHandler InternetConnectivityStatusChanged;

        #endregion

        #region variables

        const int SOCKET_SEND_TIMEOUT = 30000; //30 sec socket timeout; application protocol NOOPs at 15 sec
        const int SOCKET_RECV_TIMEOUT = 90000; //keep socket open for long time to allow tunnelling requests between time

        BinaryID _localPeerID;
        BitChatNetworkChannelRequest _channelRequestHandler;
        ProxyNetworkPeersAvailable _proxyNetworkPeersHandler;

        Dictionary<IPEndPoint, object> _makeConnectionList = new Dictionary<IPEndPoint, object>();
        Dictionary<IPEndPoint, object> _makeVirtualConnectionList = new Dictionary<IPEndPoint, object>();

        Dictionary<IPEndPoint, Connection> _connectionListByConnectionID = new Dictionary<IPEndPoint, Connection>();
        Dictionary<BinaryID, Connection> _connectionListByPeerID = new Dictionary<BinaryID, Connection>();

        //tcp
        TcpListener _tcpListener;
        Thread _tcpListenerThread;

        //internet connectivity
        const int CONNECTIVITY_CHECK_TIMER_INTERVAL = 60 * 1000;

        Uri CONNECTIVITY_CHECK_WEB_SERVICE = new Uri("https://bitchat.im/connectivity/check.aspx");
        Timer _connectivityCheckTimer;
        InternetConnectivityStatus _internetStatus = InternetConnectivityStatus.Unknown;
        InternetGatewayDevice _upnpDevice;
        UPnPDeviceStatus _upnpDeviceStatus = UPnPDeviceStatus.Unknown;

        int _localPort;
        IPAddress _localLiveIP = null;
        IPAddress _upnpExternalIP = null;
        int _upnpExternalPort = -1;

        bool _isWebAccessible = false;
        bool _webCheckError = false;
        bool _webCheckSuccess = false;
        IPEndPoint _webCheckExternalEP = null;
        bool _receivedLiveInboundConnection = false;

        #endregion

        #region constructor

        public ConnectionManager(int localPort, BitChatNetworkChannelRequest channelRequestHandler, ProxyNetworkPeersAvailable proxyNetworkPeersHandler)
        {
            try
            {
                _tcpListener = new TcpListener(IPAddress.Any, localPort);
                _tcpListener.Start(10);
            }
            catch
            {
                _tcpListener = new TcpListener(IPAddress.Any, 0);
                _tcpListener.Start(10);
            }

            _localPort = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;
            _localPeerID = BinaryID.GenerateRandomID160();
            _channelRequestHandler = channelRequestHandler;
            _proxyNetworkPeersHandler = proxyNetworkPeersHandler;

            //start accepting connections
            _tcpListenerThread = new Thread(AcceptTcpConnectionAsync);
            _tcpListenerThread.IsBackground = true;
            _tcpListenerThread.Start();

            //start upnp process
            _connectivityCheckTimer = new Timer(ConnectivityCheckTimerCallback, null, 1000, Timeout.Infinite);
        }

        #endregion

        #region IDisposable

        ~ConnectionManager()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        bool _disposed = false;

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                //shutdown tcp
                if (_tcpListener != null)
                    _tcpListener.Stop();

                if (_tcpListenerThread != null)
                    _tcpListenerThread.Abort();

                //shutdown upnp port mapping
                if (_connectivityCheckTimer != null)
                {
                    _connectivityCheckTimer.Dispose();
                    _connectivityCheckTimer = null;
                }

                //stop channel services
                List<Connection> connectionList = new List<Connection>();

                lock (_connectionListByConnectionID)
                {
                    foreach (Connection connection in _connectionListByConnectionID.Values)
                        connectionList.Add(connection);
                }

                foreach (Connection connection in connectionList)
                {
                    try
                    {
                        connection.Dispose();
                    }
                    catch
                    { }
                }

                _disposed = true;
            }
        }

        #endregion

        #region private

        private void AcceptTcpConnectionAsync()
        {
            try
            {
                do
                {
                    Socket socket = _tcpListener.AcceptSocket();

                    try
                    {
                        socket.NoDelay = true;
                        socket.SendTimeout = SOCKET_SEND_TIMEOUT;
                        socket.ReceiveTimeout = SOCKET_RECV_TIMEOUT;

                        IPEndPoint remoteEP = socket.RemoteEndPoint as IPEndPoint;

                        if (!_receivedLiveInboundConnection)
                            _receivedLiveInboundConnection = !NetUtilities.IsPrivateIPv4(remoteEP.Address);

                        AcceptConnectionInitiateProtocol(new NetworkStream(socket), remoteEP);
                    }
                    catch
                    { }
                }
                while (true);
            }
            catch
            {
            }
        }

        private Connection AddConnection(Stream networkStream, BinaryID remotePeerID, IPEndPoint remotePeerEP)
        {
            lock (_connectionListByConnectionID)
            {
                //check for self
                if (_localPeerID.Equals(remotePeerID))
                    return null;

                //check for existing connection by connection id
                if (_connectionListByConnectionID.ContainsKey(remotePeerEP))
                {
                    Connection existingConnection = _connectionListByConnectionID[remotePeerEP];

                    //check for virtual vs real connection
                    bool currentIsVirtual = Connection.IsVirtualConnection(networkStream);
                    bool existingIsVirtual = existingConnection.IsVirtual;

                    if (existingIsVirtual && !currentIsVirtual)
                    {
                        //existing is virtual and current is real; remove existing connection
                        existingConnection.Dispose();
                    }
                    else if (currentIsVirtual)
                    {
                        //existing is real/virtual and current is virtual; keep existing connection
                        return null;
                    }
                }
                else if (_connectionListByPeerID.ContainsKey(remotePeerID)) //check for existing connection by peer id
                {
                    Connection existingConnection = _connectionListByPeerID[remotePeerID];

                    //check for virtual vs real connection
                    bool currentIsVirtual = Connection.IsVirtualConnection(networkStream);
                    bool existingIsVirtual = existingConnection.IsVirtual;

                    if (existingIsVirtual && !currentIsVirtual)
                    {
                        //existing is virtual and current is real; remove existing connection
                        existingConnection.Dispose();
                    }
                    else if (currentIsVirtual)
                    {
                        //existing is real/virtual and current is virtual; keep existing connection
                        return null;
                    }

                    //compare existing and new peer ip end-point
                    if (AllowNewConnection(existingConnection.RemotePeerEP, remotePeerEP))
                    {
                        //remove existing connection and allow new connection
                        existingConnection.Dispose();
                    }
                    else
                    {
                        //keep existing connection
                        return null;
                    }
                }

                //add connection
                Connection connection = new Connection(networkStream, remotePeerID, remotePeerEP, this, _channelRequestHandler, _proxyNetworkPeersHandler);
                _connectionListByConnectionID.Add(remotePeerEP, connection);
                _connectionListByPeerID.Add(remotePeerID, connection);

                //start service
                connection.Start();

                return connection;
            }
        }

        private bool AllowNewConnection(IPEndPoint existingIP, IPEndPoint newIP)
        {
            if (existingIP.AddressFamily != newIP.AddressFamily)
            {
                if (existingIP.AddressFamily == AddressFamily.InterNetwork)
                    return false;
            }

            if (existingIP.AddressFamily == AddressFamily.InterNetwork)
            {
                if (NetUtilities.IsPrivateIPv4(existingIP.Address))
                    return false;
            }

            return true;
        }

        internal bool IsPeerConnectionAvailable(IPEndPoint remotePeerEP)
        {
            lock (_connectionListByConnectionID)
            {
                return _connectionListByConnectionID.ContainsKey(remotePeerEP);
            }
        }

        internal Connection GetExistingConnection(IPEndPoint remotePeerEP)
        {
            lock (_connectionListByConnectionID)
            {
                if (_connectionListByConnectionID.ContainsKey(remotePeerEP))
                    return _connectionListByConnectionID[remotePeerEP];

                return null;
            }
        }

        internal void RemoveConnection(Connection connection)
        {
            lock (_connectionListByConnectionID)
            {
                _connectionListByConnectionID.Remove(connection.RemotePeerEP);
                _connectionListByPeerID.Remove(connection.RemotePeerID);
            }
        }

        private Connection MakeVirtualConnection(IPEndPoint remotePeerEP)
        {
            //ping all connected peer channels
            object lockObject = new object();
            Connection[] placeholder = new Connection[] { null };

            lock (lockObject)
            {
                lock (_connectionListByConnectionID)
                {
                    if (_connectionListByConnectionID.Count == 0)
                        throw new Exception("No peer available for virtual connection.");

                    foreach (Connection connection in _connectionListByConnectionID.Values)
                        ThreadPool.QueueUserWorkItem(RequestPeerStatusAsync, new object[] { connection, remotePeerEP, lockObject, placeholder });
                }

                if (!Monitor.Wait(lockObject, 20000))
                    throw new Exception("Timed out while waiting for available peers for virtual connection.");

                Connection proxyPeerConnection = placeholder[0];

                //create tunnel via proxy peer
                Stream proxyNetworkStream = proxyPeerConnection.RequestProxyTunnelChannel(remotePeerEP);

                //make new connection protocol begins
                return MakeConnectionInitiateProtocol(proxyNetworkStream, remotePeerEP);
            }
        }

        private void RequestPeerStatusAsync(object state)
        {
            object[] param = state as object[];

            Connection connection = param[0] as Connection;
            IPEndPoint remotePeerEP = param[1] as IPEndPoint;
            object lockObject = param[2];
            Connection[] placeholder = param[3] as Connection[];

            try
            {
                if (connection.RequestPeerStatus(remotePeerEP))
                {
                    lock (lockObject)
                    {
                        placeholder[0] = connection;
                        Monitor.Pulse(lockObject);
                    }
                }
            }
            catch
            { }
        }

        internal Connection AcceptConnectionInitiateProtocol(Stream networkStream, IPEndPoint remotePeerEP)
        {
            //read version
            int version = networkStream.ReadByte();

            switch (version)
            {
                case 1:
                    //read service port
                    byte[] remoteServicePort = new byte[2];
                    networkStream.Read(remoteServicePort, 0, 2);
                    remotePeerEP = new IPEndPoint(remotePeerEP.Address, BitConverter.ToUInt16(remoteServicePort, 0));

                    //read peer id
                    byte[] peerID = new byte[20];
                    networkStream.Read(peerID, 0, 20);
                    BinaryID remotePeerID = new BinaryID(peerID);

                    //add
                    Connection connection = AddConnection(networkStream, remotePeerID, remotePeerEP);
                    if (connection != null)
                    {
                        //send ok
                        networkStream.WriteByte(0);
                        networkStream.Write(_localPeerID.ID, 0, 20);
                    }
                    else
                    {
                        //send cancel
                        networkStream.WriteByte(1);
                        networkStream.Close();
                    }
                    return connection;

                default:
                    networkStream.Close();
                    throw new IOException("Cannot accept remote connection: protocol version not supported.");
            }
        }

        private Connection MakeConnectionInitiateProtocol(Stream networkStream, IPEndPoint remotePeerEP)
        {
            try
            {
                //send version
                networkStream.WriteByte(1);

                //send service port
                networkStream.Write(BitConverter.GetBytes(Convert.ToUInt16(GetExternalPort())), 0, 2);

                //send peer id
                networkStream.Write(_localPeerID.ID, 0, 20);

                //read response
                int response = networkStream.ReadByte();
                if (response == 0)
                {
                    byte[] buffer = new byte[20];
                    networkStream.Read(buffer, 0, 20);
                    BinaryID remotePeerID = new BinaryID(buffer);

                    Connection connection = AddConnection(networkStream, remotePeerID, remotePeerEP);
                    if (connection == null)
                    {
                        //check for existing connection again!
                        Connection existingConnection = GetExistingConnection(remotePeerEP);
                        if (existingConnection != null)
                        {
                            networkStream.Dispose();
                            return existingConnection;
                        }

                        throw new IOException("Cannot connect to remote peer: connection already exists.");
                    }

                    return connection;
                }
                else
                {
                    Thread.Sleep(500); //wait so that other thread gets time to add his connection in list so that this thread can pick same connection to proceed

                    //check for existing connection again!
                    Connection existingConnection = GetExistingConnection(remotePeerEP);
                    if (existingConnection != null)
                    {
                        networkStream.Dispose();
                        return existingConnection;
                    }

                    throw new IOException("Cannot connect to remote peer: request rejected.");
                }
            }
            catch
            {
                networkStream.Dispose();
                throw;
            }
        }

        #endregion

        #region internet connectivity

        private void ConnectivityCheckTimerCallback(object state)
        {
            InternetConnectivityStatus newInternetStatus = InternetConnectivityStatus.Unknown;
            UPnPDeviceStatus newUPnPStatus = UPnPDeviceStatus.Unknown;

            try
            {
                NetworkInfo defaultNetworkInfo = NetUtilities.GetDefaultNetworkInfo();
                if (defaultNetworkInfo == null)
                {
                    //no internet available;
                    newInternetStatus = InternetConnectivityStatus.NoInternetConnection;
                    return;
                }

                if (defaultNetworkInfo.IsPublicIP)
                {
                    //public ip so, direct internet connection available
                    newInternetStatus = InternetConnectivityStatus.DirectInternetConnection;
                    _localLiveIP = defaultNetworkInfo.LocalIP;
                    return;
                }
                else
                {
                    _localLiveIP = null;
                }

                //check for upnp device
                try
                {
                    if ((_upnpDevice == null) || (!_upnpDevice.NetworkBroadcastAddress.Equals(defaultNetworkInfo.BroadcastIP)))
                        _upnpDevice = InternetGatewayDevice.Discover(defaultNetworkInfo.BroadcastIP, 30000);

                    newInternetStatus = InternetConnectivityStatus.NatInternetConnectionViaUPnPRouter;
                }
                catch
                {
                    newInternetStatus = InternetConnectivityStatus.NatInternetConnection;
                    newUPnPStatus = UPnPDeviceStatus.DeviceNotFound;
                    throw;
                }

                //find external ip from router
                try
                {
                    _upnpExternalIP = _upnpDevice.GetExternalIPAddress();

                    if (NetUtilities.IsPrivateIPv4(_upnpExternalIP))
                    {
                        newUPnPStatus = UPnPDeviceStatus.ExternalIpPrivate;
                        return; //no use of doing port forwarding for private upnp ip address
                    }
                }
                catch
                {
                    _upnpExternalIP = null;
                }

                //do upnp port forwarding
                IPEndPoint LocalNetworkEP = new IPEndPoint(defaultNetworkInfo.LocalIP, _localPort);
                int externalPort = _localPort;
                bool isTCPMapped = false;

                try
                {
                    int loopCount = 0;

                    while (true)
                    {
                        PortMappingEntry portMap = _upnpDevice.GetSpecificPortMappingEntry(ProtocolType.Tcp, externalPort);

                        if (portMap == null)
                            break; //port available

                        if (portMap.InternalEP.Equals(LocalNetworkEP))
                        {
                            //port already mapped with us
                            isTCPMapped = true;
                            newUPnPStatus = UPnPDeviceStatus.PortForwarded;
                            _upnpExternalPort = externalPort;
                            break;
                        }

                        //find new port for mapping
                        if (externalPort < ushort.MaxValue)
                            externalPort++;
                        else
                            externalPort = 1024;

                        if (loopCount > ushort.MaxValue)
                            return;

                        loopCount++;
                    }
                }
                catch { }

                if (!isTCPMapped)
                {
                    try
                    {
                        _upnpDevice.AddPortMapping(ProtocolType.Tcp, externalPort, LocalNetworkEP, "Bit Chat");

                        newUPnPStatus = UPnPDeviceStatus.PortForwarded;
                        _upnpExternalPort = externalPort;

                        Debug.Write("BitChatClient.ConnectivityCheckTimerCallback", "tcp port mapped " + externalPort);
                    }
                    catch
                    {
                        try
                        {
                            _upnpDevice.DeletePortMapping(ProtocolType.Tcp, externalPort);
                            _upnpDevice.AddPortMapping(ProtocolType.Tcp, externalPort, LocalNetworkEP, "Bit Chat");

                            newUPnPStatus = UPnPDeviceStatus.PortForwarded;
                            _upnpExternalPort = externalPort;

                            Debug.Write("BitChat.ConnectivityCheckTimerCallback", "tcp port mapped " + externalPort);
                        }
                        catch
                        {
                            newUPnPStatus = UPnPDeviceStatus.PortForwardingFailed;
                            _upnpExternalPort = -1;
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.Write("BitChat.ConnectivityCheckTimerCallback", ex);
            }
            finally
            {
                //validate change in status by performing tests
                if ((_internetStatus != newInternetStatus) || (_upnpDeviceStatus != newUPnPStatus))
                {
                    _isWebAccessible = WebUtilities.IsWebAccessible();

                    switch (newInternetStatus)
                    {
                        case InternetConnectivityStatus.DirectInternetConnection:
                            if (_isWebAccessible)
                            {
                                if (!DoWebCheckIncomingConnection(_localPort))
                                    _localLiveIP = null;
                            }
                            else
                            {
                                newInternetStatus = InternetConnectivityStatus.NoInternetConnection;
                                _localLiveIP = null;
                            }
                            break;

                        case InternetConnectivityStatus.NatInternetConnection:
                            if (_isWebAccessible)
                            {
                                DoWebCheckIncomingConnection(_localPort);
                            }
                            else
                            {
                                newInternetStatus = InternetConnectivityStatus.NoInternetConnection;
                            }
                            break;

                        case InternetConnectivityStatus.NatInternetConnectionViaUPnPRouter:
                            if (_isWebAccessible)
                            {
                                if (newUPnPStatus == UPnPDeviceStatus.PortForwarded)
                                {
                                    if (!DoWebCheckIncomingConnection(_upnpExternalPort))
                                        newUPnPStatus = UPnPDeviceStatus.PortForwardedNotAccessible;
                                }
                            }
                            else
                            {
                                newInternetStatus = InternetConnectivityStatus.NoInternetConnection;
                                newUPnPStatus = UPnPDeviceStatus.Unknown;
                                _upnpExternalIP = null;
                                _upnpExternalPort = -1;
                            }

                            break;

                        default:
                            _localLiveIP = null;
                            _upnpExternalIP = null;
                            _upnpExternalPort = -1;
                            break;
                    }

                    _internetStatus = newInternetStatus;
                    _upnpDeviceStatus = newUPnPStatus;

                    if (InternetConnectivityStatusChanged != null)
                        InternetConnectivityStatusChanged(this, EventArgs.Empty);
                }

                //schedule next check
                if (_connectivityCheckTimer != null)
                {
                    switch (_upnpDeviceStatus)
                    {
                        case UPnPDeviceStatus.DeviceNotFound:
                        case UPnPDeviceStatus.PortForwardingFailed:
                            _connectivityCheckTimer.Change(10000, Timeout.Infinite);
                            break;

                        default:
                            _connectivityCheckTimer.Change(CONNECTIVITY_CHECK_TIMER_INTERVAL, Timeout.Infinite);
                            break;
                    }
                }
            }
        }

        private bool DoWebCheckIncomingConnection(int externalPort)
        {
            try
            {
                using (WebClient client = new WebClient())
                {
                    client.QueryString.Add("port", externalPort.ToString());

                    using (MemoryStream mS = new MemoryStream(client.DownloadData(CONNECTIVITY_CHECK_WEB_SERVICE)))
                    {
                        _webCheckError = false;
                        _webCheckSuccess = (mS.ReadByte() == 1);

                        if (!_webCheckSuccess)
                            _receivedLiveInboundConnection = false;

                        switch (mS.ReadByte())
                        {
                            case 1: //ipv4
                                {
                                    byte[] ipv4 = new byte[4];
                                    byte[] port = new byte[2];

                                    mS.Read(ipv4, 0, 4);
                                    mS.Read(port, 0, 2);

                                    _webCheckExternalEP = new IPEndPoint(new IPAddress(ipv4), BitConverter.ToUInt16(port, 0));
                                }
                                break;

                            case 2: //ipv6
                                {
                                    byte[] ipv6 = new byte[16];
                                    byte[] port = new byte[2];

                                    mS.Read(ipv6, 0, 16);
                                    mS.Read(port, 0, 2);

                                    _webCheckExternalEP = new IPEndPoint(new IPAddress(ipv6), BitConverter.ToUInt16(port, 0));
                                }
                                break;

                            default:
                                _webCheckExternalEP = null;
                                break;
                        }
                    }
                }
            }
            catch
            {
                _webCheckError = true;
                _webCheckSuccess = false;
                _webCheckExternalEP = null;
            }

            return _webCheckSuccess || _webCheckError;
        }

        public IPEndPoint GetExternalEP()
        {
            if (_webCheckSuccess)
                return _webCheckExternalEP;

            switch (_internetStatus)
            {
                case InternetConnectivityStatus.DirectInternetConnection:
                    if (_localLiveIP == null)
                        return null;
                    else
                        return new IPEndPoint(_localLiveIP, _localPort);

                case InternetConnectivityStatus.NatInternetConnectionViaUPnPRouter:
                    switch (_upnpDeviceStatus)
                    {
                        case UPnPDeviceStatus.PortForwarded:
                            if (!_webCheckError && !_webCheckSuccess)
                                return null;

                            if (_upnpExternalIP == null)
                                return null;
                            else
                                return new IPEndPoint(_upnpExternalIP, _upnpExternalPort);

                        default:
                            return null;
                    }

                default:
                    return null;
            }
        }

        public int GetExternalPort()
        {
            IPEndPoint externalEP = GetExternalEP();
            if (externalEP != null)
                return externalEP.Port;
            else
                return _localPort;
        }

        #endregion

        #region public

        public Connection MakeConnection(IPEndPoint remotePeerEP)
        {
            //prevent multiple connection requests to same remote end-point
            lock (_makeConnectionList)
            {
                if (_makeConnectionList.ContainsKey(remotePeerEP))
                    throw new BitChatException("Connection attempt for end-point already in progress.");

                _makeConnectionList.Add(remotePeerEP, null);
            }

            try
            {
                //check if self
                IPEndPoint externalEP = GetExternalEP();
                if ((externalEP != null) && externalEP.Equals(remotePeerEP))
                    throw new IOException("Cannot connect to remote port: self connection.");

                //check existing connection
                Connection existingConnection = GetExistingConnection(remotePeerEP);
                if (existingConnection != null)
                    return existingConnection;

                try
                {
                    //try new tcp connection
                    TcpClient client = new TcpClient();
                    client.Connect(remotePeerEP);

                    client.NoDelay = true;
                    client.SendTimeout = SOCKET_SEND_TIMEOUT;
                    client.ReceiveTimeout = SOCKET_RECV_TIMEOUT;

                    return MakeConnectionInitiateProtocol(new NetworkStream(client.Client), remotePeerEP);
                }
                catch (SocketException)
                {
                    //try virtual connection
                    return MakeVirtualConnection(remotePeerEP);
                }
            }
            finally
            {
                lock (_makeConnectionList)
                {
                    _makeConnectionList.Remove(remotePeerEP);
                }
            }
        }

        public Connection MakeVirtualConnection(Connection viaConnection, IPEndPoint remotePeerEP)
        {
            //prevent multiple virtual connection requests to same remote end-point
            lock (_makeVirtualConnectionList)
            {
                if (_makeVirtualConnectionList.ContainsKey(remotePeerEP))
                    throw new BitChatException("Connection attempt for end-point already in progress.");

                _makeVirtualConnectionList.Add(remotePeerEP, null);
            }

            try
            {
                //check if self
                IPEndPoint externalEP = GetExternalEP();
                if ((externalEP != null) && externalEP.Equals(remotePeerEP))
                    throw new IOException("Cannot connect to remote port: self connection.");

                //check existing connection
                Connection existingConnection = GetExistingConnection(remotePeerEP);
                if (existingConnection != null)
                    return existingConnection;

                //create tunnel via proxy peer
                Stream proxyNetworkStream = viaConnection.RequestProxyTunnelChannel(remotePeerEP);

                //make new connection protocol begins
                return MakeConnectionInitiateProtocol(proxyNetworkStream, remotePeerEP);
            }
            finally
            {
                lock (_makeVirtualConnectionList)
                {
                    _makeVirtualConnectionList.Remove(remotePeerEP);
                }
            }
        }

        #endregion

        #region properties

        public BinaryID LocalPeerID
        { get { return _localPeerID; } }

        public int LocalPort
        { get { return _localPort; } }

        public InternetConnectivityStatus InternetStatus
        { get { return _internetStatus; } }

        public UPnPDeviceStatus UPnPStatus
        { get { return _upnpDeviceStatus; } }

        public IPEndPoint UPnPExternalEP
        {
            get
            {
                if (_upnpExternalPort > -1)
                    return new IPEndPoint(_upnpExternalIP, _upnpExternalPort);
                else
                    return new IPEndPoint(_upnpExternalIP, 0);
            }
        }

        #endregion
    }
}