﻿using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Client.Client;
using ChatLibrary.Helper;
using NAudio.Wave;
using Timer = System.Timers.Timer;
namespace Client.Core
{
    public class ChatClient
    {
        #region Fields

        private readonly TcpClient server;
        private readonly Socket clientSocket;
        
        private WaveInEvent sourceStream;
        private WaveOut recievedStream;
        private BufferedWaveProvider waveProvider;
        private readonly IPEndPoint localEndPoint;
        private IPEndPoint remoteEndPoint;
        private Thread udpReceiveThread;
        private Thread tcpRecieveThread;
        //private Thread heartBeatThread;
        private string udpSubscriber;
        private bool udpConnectionActive;
        #endregion

        #region Events
        public event EventHandler UserListReceived;
        public event EventHandler MessageReceived;
        public event EventHandler CallRecieved;
        public event EventHandler CallRequestResponded;
        #endregion

        #region Properties

        public string ClientAddress { get; set; }
        public string ServerAddress { get; set; }
        public string ServerName { get; set; }
        public string UserName { get; set; }
        public bool IsConnected { get; set; }

        #endregion

        #region Consructor
        /// <summary>
        /// Connect to server
        /// </summary>
        /// <param name="port"></param>
        /// <param name="serverAddress"></param>
        /// <param name="userName"></param>
        public ChatClient(int port, string serverAddress, string userName)
        {
            try
            {
                server = new TcpClient(serverAddress,port);
                IsConnected = true;
                // Creating new udp client socket
                clientSocket = new Socket(AddressFamily.InterNetwork,
                    SocketType.Dgram, ProtocolType.Udp);
                localEndPoint = GetHostEndPoint();
                ServerAddress = serverAddress;
                UserName = userName;
            }
            catch (SocketException)
            {
                MessageBox.Show(@"Unable to connect to server");
                return;
            }
           // Send username to server
            var bytes = Encoding.Unicode.GetBytes(userName);
            server.Client.Send(bytes);
        }
        #endregion

        #region Methods

        private void BindSocket()
        {
            if (!clientSocket.IsBound)
                clientSocket.Bind(localEndPoint);
        }

        private IPEndPoint GetHostEndPoint()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            var ipAddress = host.AddressList.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
            if (ipAddress == null) 
                return null;
            var random = new Random();
            var endPoint = new IPEndPoint(ipAddress,random.Next(65000,65536));
            ClientAddress = string.Format("{0}:{1}",endPoint.Address,endPoint.Port);
            return endPoint;
        }


        public bool Init()
        {
            //Receive list of users online 
            if (!ReceiveUsersList())
            {
                return false;
            }
            //heartBeatThread = new Thread(HeartBeat);
            //heartBeatThread.Start();
            waveProvider = new BufferedWaveProvider(new WaveFormat(8000, 16, WaveIn.GetCapabilities(0).Channels));
            recievedStream = new WaveOut();
            recievedStream.Init(waveProvider);

            tcpRecieveThread = new Thread(RecieveFromServer) {Priority = ThreadPriority.Highest};
            tcpRecieveThread.Start();
            return true;
        }

        private void RecieveFromServer()
        {
            var state = new ChatHelper.StateObject
            {
                WorkSocket = server.Client
            };

            server.Client.BeginReceive(state.Buffer, 0, ChatHelper.StateObject.BufferSize, 0,
                OnReceive, state);
        }

        /// <summary>
        /// Heartbeat request to server
        /// </summary>
        private void HeartBeat()
        {
            var data = new Data {Command = Command.Heartbeat, From = UserName};
            Timer timer = null;
            while (IsConnected)
            {
                if (timer == null)
                {
                    timer = new Timer(1000);
                    timer.Elapsed += (sender, args) =>
                    {
                        try
                        {
                            server.Client.Send(data.ToByte());
                        }
                        catch
                        {
                            server.Client.Disconnect(true);
                        }
                   };
                }
                timer.Enabled = true;
            }
            
        }

        private bool ReceiveUsersList()
        {
            var bytes = new byte[1024];
            server.Client.Receive(bytes);
            var data = new Data(bytes);

            var serverMessage = data.Message.Split(new[] { '|' }, StringSplitOptions.None);

            if (data.Command == Command.NameExist)
            {
                MessageBox.Show(string.Format("Name \"{0}\" already exist on server", serverMessage[1]));
                return false;
            }

            ServerName = serverMessage[3];
            
            OnUserListReceived(serverMessage);

            return true;
        }

        public void OnReceive(IAsyncResult ar)
        {
            var state = ar.AsyncState as ChatHelper.StateObject;
            if (state == null)
                return;
            var handler = state.WorkSocket;
            if (!handler.Connected)
                return;
            try
            {
                var bytesRead = handler.EndReceive(ar);
                if (bytesRead <= 0)
                    return;
                ParseMessage(new Data(state.Buffer));
                
                server.Client.BeginReceive(state.Buffer, 0, ChatHelper.StateObject.BufferSize, 0, OnReceive, state);
            }
            catch (SocketException)
            {
                server.Client.Disconnect(true);
            }
        }


        public void OnUdpRecieve(IAsyncResult ar)
        {
            var state = ar.AsyncState as ChatHelper.StateObject;
            if (state == null) 
                return;
            var handler = clientSocket;

            try
            {
                var bytesRead = handler.EndReceive(ar);
                waveProvider.AddSamples(state.Buffer, 0, bytesRead);
                recievedStream.Play();

                var ep = (EndPoint)localEndPoint;
                if (udpConnectionActive)
                    handler.BeginReceiveFrom(state.Buffer, 0, ChatHelper.StateObject.BufferSize, SocketFlags.None, ref ep, OnUdpRecieve, state);
            }
            catch (Exception)
            {
                // remote user disconnected
                //clientSocket.Close();
            }
        }

        /// <summary>
        /// Parse received message
        /// </summary>
        /// <param name="message"></param>
        public void ParseMessage(Data message)
        {
            switch (message.Command)
            {
                case Command.SendMessage:
                    OnMessageReceived(message.Message, message.From);
                break;

                case Command.Broadcast:
                    OnUserListReceived(message.Message.Split('|'));
                break;

                case Command.Call:
                    if (!udpConnectionActive)
                        OnCallRecieved(message.From, message.ClientAddress);
                    SendResponse(Command.Busy);
                break;

                case Command.AcceptCall:
                case Command.CancelCall:
                case Command.EndCall:
                    ParseResponse(message.From,message.Command,message.ClientAddress);
                    OnCallResponseReceived(message.Command);
                break;
            }
        }

        private void ParseResponse(string user,Command response,string address)
        {
            switch (response)
            {
                case Command.AcceptCall:
                    udpSubscriber = user;
                    StartVoiceChat(address);
                break;
                case Command.EndCall:
                    EndChat(false);
                break;
            }
        }

        private void StartVoiceChat(string address)
        {
            var splittedAddress = address.Split(':');
            var ip = splittedAddress[0];
            var port = splittedAddress[1];
            
            BindSocket();

            remoteEndPoint = new IPEndPoint(IPAddress.Parse(ip),Int32.Parse(port));

            sourceStream = new WaveInEvent
            {
                DeviceNumber = 0,
                WaveFormat = new WaveFormat(8000, 16, WaveIn.GetCapabilities(0).Channels)
            };

            udpConnectionActive = true;

            sourceStream.DataAvailable += sourceStream_DataAvailable;
            sourceStream.StartRecording();

            udpReceiveThread = new Thread(ReceiveUdpData);
            udpReceiveThread.Start();
        }

        private void sourceStream_DataAvailable(object sender, WaveInEventArgs e)
        {
            if (sourceStream == null)
                return;
            SendUdpData(e.Buffer, e.BytesRecorded);
        }


        private void SendUdpData(byte[] buf, int bytesRecorded)
        {
            try
            {
                if (!udpConnectionActive)
                {
                    sourceStream.StopRecording();
                    return;
                }
                var ep = remoteEndPoint as EndPoint;
                clientSocket.SendTo(buf, 0, bytesRecorded, SocketFlags.None, ep);
            }
            catch (Exception)
            {
                sourceStream.StopRecording();    
            }
        }

        private void ReceiveUdpData()
        {
            try
            {
                var state = new ChatHelper.StateObject { WorkSocket = clientSocket };
                var ep = remoteEndPoint as EndPoint;
                clientSocket.BeginReceiveFrom(state.Buffer, 0, ChatHelper.StateObject.BufferSize, SocketFlags.None, ref ep,
                    OnUdpRecieve, state);
            }
            catch (Exception)
            {
                //clientSocket.Close();
            }
            
        }

        /// <summary>
        /// Send message
        /// </summary>
        /// <param name="message"></param>
        /// <param name="recipient"></param>
        public void SendMessage(string message,string recipient)
        {
            var data = new Data {Command = Command.SendMessage, To = recipient, From = UserName, Message = message};
            server.Client.Send(data.ToByte());
        }
        /// <summary>
        /// Call to user
        /// </summary>
        /// <param name="recipient"></param>
        public void SendChatRequest(string recipient)
        {
            var data = new Data {Command = Command.Call, To = recipient, From = UserName, ClientAddress = ClientAddress};
            server.Client.Send(data.ToByte());
        }
        /// <summary>
        /// Send call response to caller
        /// </summary>
        /// <param name="response"></param>
        private void SendResponse(Command response)
        {
            var data = new Data {Command = response, To = udpSubscriber, From = UserName, ClientAddress = ClientAddress};
            server.Client.Send(data.ToByte());
        }


        /// <summary>
        /// Closes server connection
        /// </summary>
        public void CloseConnection()
        {
            IsConnected = false;
            server.Client.Close();
        }
        /// <summary>
        /// Ends UDP connection
        /// </summary>
        /// <param name="requestNeeded"></param>
        public void EndChat(bool requestNeeded)
        {
            if (requestNeeded)
                SendResponse(Command.EndCall);
            udpConnectionActive = false;
        }


        public void AnswerIncomingCall(string caller, string address, Command answer)
        {
            var data = new Data {Command = answer, From = UserName, To = caller, ClientAddress = ClientAddress};
            if (answer == Command.AcceptCall)
            {
                udpSubscriber = caller;
                StartVoiceChat(address);
            }
            server.Client.Send(data.ToByte());
        }

        #endregion

        #region Event Invokers

        protected virtual void OnUserListReceived(string[] serverMessage)
        {
            var handler = UserListReceived;
            if (handler != null)
                handler(serverMessage[0], new ServerEventArgs(serverMessage[1], serverMessage[2], serverMessage[3]));
        }

        protected virtual void OnMessageReceived(string message, string sender)
        {
            var handler = MessageReceived;
            if (handler != null) handler(sender, new ServerEventArgs(message));
        }

        protected virtual void OnCallRecieved(string caller, string address)
        {
            var handler = CallRecieved;
            if (handler != null) handler(address, new ServerEventArgs(caller));
        }

        protected virtual void OnCallResponseReceived(Command response)
        {
            var handler = CallRequestResponded;
            if (handler != null) handler(response, EventArgs.Empty);
        }

        #endregion

       
    }

    
}