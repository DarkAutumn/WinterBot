﻿using IrcDotNet;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;
using WinterBotLogging;

namespace Winter
{
    public enum Importance
    {
        Low,
        Med,
        High
    }

    public enum ConnectResult
    {
        Success,
        LoginFailed,
        NetworkFailed,
        Failed,
    }

    /// <summary>
    /// A Twitch.tv IRC client.
    /// 
    /// This class represents a single channel on the twitch.tv IRC server.  You may find
    /// it surprising that we do not allow multiple chat channels.  This is because twitch
    /// IRC gives asynchronous events for things like informing if a user is a subscriber,
    /// turbo user, or if they were banned or timed out.  Unfortunately, this is not done
    /// for a specific IRC channel, so there is no way to associate the chat clear/subscriber
    /// with a particular chat channel.
    /// 
    /// The end effect is that we need to create a new IRC session for every channel we
    /// monitor (if you care about accurate per-channel timeouts and subscribers...which
    /// we do).  This means if you want to monitor more than one channel at once, you need
    /// to have more than one TwitchClient instance (and thus multiple IRC connections).
    /// 
    /// Note that aside from the Connect function, all events and callbacks occur
    /// asynchronously, and any event handlers you register will be called from another
    /// thread than the one creating it.
    /// </summary>
    public class TwitchClient
    {
        #region Events
        /// <summary>
        /// Fired when chat was cleared for a specific user (this means they were either
        /// timed out or banned, but there isn't a way to know which...or how long the
        /// timeout was).
        /// </summary>
        public event UserEventHandler InformChatClear;

        /// <summary>
        /// Fired when twitch informs us that a user is a Twitch Turbo user.
        /// </summary>
        public event UserEventHandler InformTurbo;

        /// <summary>
        /// Fired when twitch informs us that a user is a subscriber to this channel.
        /// </summary>
        public event UserEventHandler InformSubscriber;

        /// <summary>
        /// Fired when twitch informs us that a user is a moderator in this channel.
        /// </summary>
        public event ModeratorEventHandler InformModerator;

        /// <summary>
        /// Fired when a user subscribes to the channel.
        /// </summary>
        public event UserEventHandler UserSubscribed;

        /// <summary>
        /// Fired when a chat message is received.
        /// </summary>
        public event MessageHandler MessageReceived;

        /// <summary>
        /// Fired when a chat action occurs (such as using /me).
        /// </summary>
        public event MessageHandler ActionReceived;

        /// <summary>
        /// Fired when the Jtv user sends a message informing the user of chat changes).
        /// </summary>
        public event MessageHandler JtvMessageReceived;
        
        /// <summary>
        /// Event handler for when messages are received from the chat channel.
        /// </summary>
        public delegate void MessageHandler(TwitchClient sender, TwitchUser user, string text);

        /// <summary>
        /// Event handler for when user-related events occur.
        /// </summary>
        /// <param name="user">The user in question.</param>
        public delegate void UserEventHandler(TwitchClient sender, TwitchUser user);
        
        /// <summary>
        /// Event handler for when users are timed out.
        /// </summary>
        public delegate void UserTimeoutHandler(TwitchClient sender, TwitchUser user, int duration);

        /// <summary>
        /// Event fired when moderator status changes for a user.
        /// </summary>
        /// <param name="sender">This object.</param>
        /// <param name="user">The user whos status is changing.</param>
        /// <param name="moderator">The moderator in question.</param>
        public delegate void ModeratorEventHandler(TwitchClient sender, TwitchUser user, bool moderator);
        #endregion

        /// <summary>
        /// ChannelData keeps track of whether users are moderators, subscribers, or
        /// twitch turbo users.  Note that this does not contain a list of ALL subs
        /// and mods, it simply keeps track of every user we've been informed of their
        /// status.
        /// </summary>
        public TwitchUsers ChannelData { get { return m_data; } }

        /// <summary>
        /// Returns the name of the stream we are connected to.
        /// </summary>
        public string Stream { get { return m_stream; } }
        public DateTime LastEvent { get; set; }

        /// <summary>
        /// Changes the delay between when a timeout is requested and when it is sent.
        /// This is to prevent a bot from timing out too fast and users getting the
        /// timeout before the message.  Delay is in milliseconds.
        /// </summary>
        public int TimeoutDelay
        {
            get
            {
                return m_timeoutDelay;
            }

            set
            {
                if (value >= 0)
                {
                    m_timeoutDelay = value;
                }
            }
        }

        public TwitchClient(TwitchUsers data)
        {
            m_data = data;
            LastEvent = DateTime.Now;

        }

        public TwitchClient()
        {
            LastEvent = DateTime.Now;
        }

        /// <summary>
        /// Connect to the given stream, returns true if we successfully connected.  Note
        /// that this function executes synchronously, and will block until fully connected
        /// to the IRC server.
        /// </summary>
        /// <param name="stream">The stream to connect to.</param>
        /// <param name="user">The twitch username this connection will use.</param>
        /// <param name="auth">The twitch API token used to log in.  This must begin with 'oauth:'.</param>
        public ConnectResult Connect(string stream, string user, string auth, int timeout = 10000)
        {
            if (m_shutdown)
                throw new InvalidOperationException("Attempted to connect while disconnecting.");

            user = user.ToLower();
            m_stream = stream.ToLower();

            if (m_data == null)
                m_data = new TwitchUsers(m_stream);

            // Create client and hook up events.            
            m_client = new IrcClient();

            m_client.Connected += client_Connected;
            m_client.UnsuccessfulLogin += m_client_UnsuccessfulLogin;
            m_client.ConnectFailed += client_ConnectFailed;
            m_client.Error += client_Error;
            m_client.Registered += client_Registered;
            m_client.ErrorMessageReceived += client_ErrorMessageReceived;
            m_client.PongReceived += m_client_PongReceived;
            m_client.PingReceived += m_client_PingReceived;

            m_flood = new FloodPreventer(this);
            m_flood.RejectedMessage += m_flood_RejectedMessage;
            m_client.FloodPreventer = m_flood;

            int currTimeout = timeout;
            DateTime started = DateTime.Now;

            m_connectedEvent.Reset();
            m_registeredEvent.Reset();
            m_joinedEvent.Reset();

            // Connect to server.
            m_client.Connect("irc.twitch.tv", 6667, false, new IrcUserRegistrationInfo()
            {
                NickName = user,
                UserName = user,
                RealName = user,
                Password = auth
            });

            // Wait for the server to connect.  The connect function on client operates asynchronously, so we
            // wait on s_connectedEvent which is set when client_Connected is called.
            if (!m_connectedEvent.Wait(currTimeout))
            {
                WriteDiagnosticMessage("Connecting to the Twitch IRC server timed out.");
                return ConnectResult.NetworkFailed;
            }

            currTimeout = timeout - (int)started.Elapsed().TotalMilliseconds;
            /// Wait for the client to be registered.
            if (!m_registeredEvent.Wait(currTimeout))
            {
                // Shouldn't really happen
                WriteDiagnosticMessage("Registration timed out.");
                return ConnectResult.Failed;
            }

            if (m_loginFailed)
                return ConnectResult.LoginFailed;

            // Attempt to join the channel.  We'll try for roughly 10 seconds to join.  This really shouldn't ever fail.
            m_client.Channels.Join("#" + m_stream);
            currTimeout = timeout - (int)started.Elapsed().TotalMilliseconds;
            if (!m_joinedEvent.Wait(currTimeout))
            {
                // Shouldn't really happen
                WriteDiagnosticMessage("Failed to join channel {0}.", m_stream);
                return ConnectResult.Failed;
            }

            TwitchSource.Log.Connected(stream);

            // This command tells twitch that we are a chat bot capable of understanding subscriber/turbo/etc
            // messages.  Without sending this raw command, we would not get that data.
            m_client.SendRawMessage("TWITCHCLIENT 3");

            UpdateMods();
            return ConnectResult.Success;
        }


        void m_client_PingReceived(object sender, IrcPingOrPongReceivedEventArgs e)
        {
            LastEvent = DateTime.Now;
        }

        void m_client_PongReceived(object sender, IrcPingOrPongReceivedEventArgs e)
        {
            TwitchSource.Log.ReceivedPong();
            LastEvent = DateTime.Now;
        }

        public void Ping()
        {
            if (CanSendMessage(Importance.Med, "PING ACTION"))
            {
                TwitchSource.Log.SentPing();
                m_client.Ping();
            }
        }

        public void Quit(int timeout=1000)
        {
            m_shutdown = true;

            if (CanSendMessage(Importance.High, "QUIT ACTION"))
            {
                TwitchSource.Log.Quit();
                m_client.Quit(timeout);
            }

            var thread = m_thread;
            if (thread != null)
            {
                thread.Join();
                thread = null;
            }

            m_shutdown = false;
        }

        public void Disconnect()
        {
            m_shutdown = true;

            m_client.Disconnect();

            var thread = m_thread;
            if (thread != null)
            {
                thread.Join();
                thread = null;
            }

            m_shutdown = false;
        }

        public void SendMessage(Importance importance, string text)
        {
            if (CanSendMessage(importance, text))
            {
                TwitchSource.Log.SentMessage(text);
                m_client.LocalUser.SendMessage(m_channel, text);
            }
        }

        public void Timeout(string user, int duration = 600)
        {
            StartTimeoutThread();

            if (duration <= 0)
                duration = 1;

            int delay = TimeoutDelay;
            if (delay <= 0)
            {
                TimeoutRaw(user, duration);
            }
            else if (delay <= 150)
            {
                Thread.Sleep(delay);
                TimeoutRaw(user, duration);
            }
            else
            {
                var request = new TimeoutRequest(this, user, duration);
                m_timeouts.Add(request);
            }
        }

        internal void TimeoutRaw(string user, int duration)
        {
            TwitchSource.Log.TimeoutUser(user, duration);
            m_client.LocalUser.SendMessage(m_channel, string.Format(".timeout {0} {1}", user, duration));
        }

        public void Ban(string user)
        {
            StartTimeoutThread();

            int delay = TimeoutDelay;
            if (delay <= 0)
            {
                BanRaw(user);
            }
            else if (delay <= 150)
            {
                Thread.Sleep(delay);
                BanRaw(user);
            }
            else
            {
                var request = new TimeoutRequest(this, user, -1);
                m_timeouts.Add(request);
            }
        }

        internal void BanRaw(string user)
        {
            TwitchSource.Log.BanUser(user);
            SendMessage(Importance.High, string.Format(".ban {0}", user));
        }

        public void Unban(string user)
        {
            // Sleep for 100 msec so that our message is sure to be received AFTER other users
            // get the message we want to clear.  Also bypass flood check.
            Thread.Sleep(100);
            TwitchSource.Log.UnbanUser(user);
            SendMessage(Importance.High, string.Format(".unban {0}", user));
        }


        void StartTimeoutThread()
        {
            if (m_thread == null)
            {
                if (Interlocked.CompareExchange(ref m_thread, new Thread(TimeoutThread), null) == null)
                {
                    m_thread.Name = "Timeout delay helper";
                    m_thread.Start();
                }
            }
        }

        private void TimeoutThread(object obj)
        {
            while (!m_shutdown)
            {
                TimeoutRequest evt;
                if (!m_timeouts.TryTake(out evt))
                {
                    Thread.Sleep(100);
                    continue;
                }
                
                ProcessNext(evt);

                while (evt.IsBan)
                {
                    TimeoutRequest next = null;
                    for (int i = 0; i < 15; i++)
                    {
                        Thread.Sleep(100);
                        if (m_shutdown)
                            return;

                        if (m_timeouts.TryTake(out next))
                        {
                            evt.Execute();
                            ProcessNext(next);
                            break;
                        }
                    }

                    if (next == null)
                    {
                        evt.Execute();
                        break;
                    }

                    evt = next;
                }
            }
        }

        private void ProcessNext(TimeoutRequest evt)
        {
            int delay = TimeoutDelay;

            delay -= (int)evt.Created.Elapsed().TotalMilliseconds;
            if (delay > 0)
                Thread.Sleep(delay);
            evt.Execute();
        }

        private bool CanSendMessage(Importance importance, string text)
        {
            if (m_flood == null)
                return false;

            if (!m_flood.ShouldSendMessage(importance, text))
            {
                WriteDiagnosticMessage("Dropped message: {0}.", text);
                return false;
            }

            return true;
        }

        void m_flood_RejectedMessage()
        {
            WriteDiagnosticMessage("Flood prevention rejected a message.");
        }

        /// <summary>
        /// This is called when someone sends a message to chat.
        /// </summary>
        /// <param name="sender">The IrcDotNet channel.</param>
        /// <param name="e">The user.</param>
        void channel_MessageReceived(object sender, IrcMessageEventArgs e)
        {
            TwitchSource.Log.ReceivedMessage(e.Source.Name, e.Text);

            LastEvent = DateTime.Now;
            if (m_lastModCheck.Elapsed().TotalHours >= 2)
                UpdateMods();

            // Twitchnotify is how subscriber messages "Soandso just subscribed!" comes in:
            if (e.Source.Name.Equals("twitchnotify", StringComparison.CurrentCultureIgnoreCase))
            {
                string text = e.Text;

                int i = text.IndexOf(" just");
                if (i > 0)
                {
                    var user = m_data.GetUser(text.Substring(0, i));
                    user.IsSubscriber = true;
                    user.IconSet = null; // Need to reparse icon set
                    OnUserSubscribed(user);
                    return;
                }
            }
            else if (e.Source.Name.Equals("jtv", StringComparison.CurrentCultureIgnoreCase))
            {
                HandleJtvMessage(e.Text);
                return;
            }

            if (e.Text.StartsWith(m_action))
                OnActionReceived(e);
            else
                OnMessageReceived(e);
        }

        /// <summary>
        /// Called when a message is received.  The only private message we care about are ones
        /// from jtv, which is how we know users are subscribers, turbo users, or if they get
        /// timed out.
        /// </summary>
        /// <param name="sender">IrcDotNet client.</param>
        /// <param name="e">IRC message event args.</param>
        private void client_LocalUser_MessageReceived(object sender, IrcMessageEventArgs e)
        {
            TwitchSource.Log.ReceivedPrivateMessage(e.Source.Name, e.Text);

            LastEvent = DateTime.Now;
            
            if (e.Source.Name.Equals("jtv", StringComparison.CurrentCultureIgnoreCase))
                HandleJtvMessage(e.Text);
        }


        private void HandleJtvMessage(string text)
        {
            switch (text[0])
            {
                case 'E':
                    ParseEmoteSet(text);
                    break;

                case 'C':
                    if (!ParseChatClear(text))
                        RawJtvMessage("Chat was cleared by a moderator");
                    break;

                case 'S':
                    ParseSpecialUser(text);
                    break;

                case 'T':
                    if (text.StartsWith("The moderators of this room are:"))
                        ParseModerators(text);
                    
                    RawJtvMessage(text);
                    break;

                case 'H':
                    // HISTORYEND
                    break;

                case 'U':
                    ParseUserColor(text);
                    break;

                default:
                    RawJtvMessage(text);
                    return;
            }
        }

        private void RawJtvMessage(string text)
        {
            var evt = JtvMessageReceived;
            if (evt != null)
            {
                var user = m_data.GetUser("jtv");
                evt(this, user, text);
            }
        }

        private void ParseUserColor(string text)
        {
            //USERCOLOR username #8A2BE2
            string[] parts = text.Split(' ');
            if (parts.Length != 3 || parts[0] != "USERCOLOR" || parts[2].Length < 2 || parts[2][0] != '#')
                return;

            var user = m_data.GetUser(parts[1]);
            user.Color = parts[2];
        }

        private void ParseModerators(string text)
        {
            // This room is now in slow mode. You may send messages every 120 seconds
            //*  The moderators of this room are: mod1, mod2, mod3

            string modMsg = "The moderators of this room are: ";
            if (text.Length <= modMsg.Length || text[4] != 'm' || text[31] != ':')
                return;

            lock (m_modSync)
            {
                TwitchUser streamer = m_data.GetUser(m_stream);

                string[] modList = text.Substring(modMsg.Length).Split(new string[] { ", " }, StringSplitOptions.RemoveEmptyEntries);
                HashSet<TwitchUser> mods = new HashSet<TwitchUser>(modList.Select(name => m_data.GetUser(name)));
                mods.Add(streamer);

                foreach (var mod in mods)
                    mod.IsModerator = true;

                var demodded = from mod in m_data.ModeratorSet
                               where !mods.Contains(mod)
                               select mod;

                foreach (var former in demodded)
                    former.IsModerator = false;

                m_data.ModeratorSet = mods;
            }
        }

        private void ParseSpecialUser(string text)
        {
            //*  SPECIALUSER username subscriber
            string[] parts = text.Split(' ');

            if (parts.Length != 3 || parts[0] != "SPECIALUSER" || parts[2] != "subscriber")
                return;

            var user = m_data.GetUser(parts[1]);
            if (!user.IsSubscriber)
            {
                user.IsSubscriber = true;
                OnInformSubscriber(user);
            }
        }

        private bool ParseChatClear(string text)
        {
            //CLEARCHAT username
            if (text.Length <= 10 || text[9] != ' ')
                return false;

            OnChatClear(text.Substring(10));
            return true;
        }

        private void ParseEmoteSet(string text)
        {
            //EMOTESET username [888,1520,2729]
            if (text.Length < 12 || text[8] != ' ')
                return;

            int end = text.IndexOf(' ', 9);
            if (end == -1)
                return;

            string username = text.Substring(9, end - 9);
            TwitchUser user = m_data.GetUser(username);
            if (user.IconSet != null)
                return;

            end += 2;
            if (end+1 >= text.Length)
                return;

            string items = text.Substring(end, text.Length - end - 1);
            int[] iconSet = (from str in items.Split(',')
                                let i = int.Parse(str)
                                orderby i
                                select i).ToArray();

            user.IconSet = iconSet;

        }

        private void UpdateMods()
        {
            m_lastModCheck = DateTime.Now;
            m_client.LocalUser.SendMessage(m_channel, ".mods");
        }

        #region Diagnostic Events
        /// <summary>
        /// Fired when a diagnostic message is generated by TwitchClient.
        /// </summary>
        public event DiagnosticHandler StatusUpdate;

        /// <summary>
        /// Fired when an exception occurs within IrcDotNet, which hopefully should never happen.
        /// </summary>
        public event ErrorHandler ErrorOccurred;

        /// <summary>
        /// Used to report diagnostic messages to listeners (that is, informative messages or
        /// errors used to track down problems with the TwitchClient).
        /// </summary>
        /// <param name="message">The diagnostic message reported.</param>
        public delegate void DiagnosticHandler(TwitchClient sender, string message);

        /// <summary>
        /// Used to report IRC errors.  This callback should really never happen unless there
        /// is a bug in IrcDotNet.
        /// </summary>
        /// <param name="error">The error event reported.</param>
        public delegate void ErrorHandler(TwitchClient sender, IrcErrorEventArgs error);
        #endregion

        #region IrcDotNet Event Handlers
        void client_ErrorMessageReceived(object sender, IrcErrorMessageEventArgs e)
        {
            WriteDiagnosticMessage("Error message: {0}", e.Message);
        }

        void client_Error(object sender, IrcErrorEventArgs e)
        {
            var error = ErrorOccurred;
            if (error != null)
                error(this, e);
        }

        void client_ConnectFailed(object sender, IrcErrorEventArgs e)
        {
            WriteDiagnosticMessage("Connection failed: {0}", e.Error);
        }

        private void client_Registered(object sender, EventArgs e)
        {
            var client = (IrcClient)sender;
            client.LocalUser.MessageReceived += client_LocalUser_MessageReceived;
            client.LocalUser.JoinedChannel += client_LocalUser_JoinedChannel;
            m_registeredEvent.Set();
        }

        private void client_LocalUser_JoinedChannel(object sender, IrcChannelEventArgs e)
        {
            m_joinedEvent.Set();
            m_channel = e.Channel;
            m_channel.MessageReceived += channel_MessageReceived;
            m_channel.UserJoined += m_channel_UserJoined;
            m_channel.UsersListReceived += m_channel_UsersListReceived;
        }

        void m_channel_UsersListReceived(object sender, EventArgs e)
        {
            foreach (var user in m_channel.Users)
            {
                CheckModeratorStatus(user);
                user.ModesChanged += ChannelUser_ModesChanged;
            }
        }


        void ChannelUser_ModesChanged(object sender, EventArgs e)
        {
            IrcChannelUser user = sender as IrcChannelUser;
            if (user != null)
                CheckModeratorStatus(user);
        }


        void m_channel_UserJoined(object sender, IrcChannelUserEventArgs e)
        {
            CheckModeratorStatus(e.ChannelUser);
            e.ChannelUser.ModesChanged += ChannelUser_ModesChanged;
        }


        private void CheckModeratorStatus(IrcChannelUser chanUser)
        {
            string username = chanUser.User.NickName;

            bool op = chanUser.Modes.Contains('o');
            TwitchUser user = m_data.GetUser(username, op);

            if (user != null)
            {
                if (op)
                {
                    if (!m_mods.Contains(user))
                    {
                        OnInformModerator(user, true);
                        m_mods.Add(user);
                    }
                }
                else
                {
                    if (m_mods.Contains(user))
                    {
                        OnInformModerator(user, false);
                        m_mods.Remove(user);
                    }
                }
            }
        }

        HashSet<TwitchUser> m_mods = new HashSet<TwitchUser>();


        protected void OnInformModerator(TwitchUser user, bool moderator)
        {
            var evt = InformModerator;
            if (evt != null)
                evt(this, user, moderator);
        }

        private void client_Connected(object sender, EventArgs e)
        {
            m_connectedEvent.Set();
        }

        void m_client_UnsuccessfulLogin(IrcClient source, IIrcMessageSource ircMessageSource, IIrcMessageTarget[] targets)
        {
            m_loginFailed = true;
            m_registeredEvent.Set();
        }
        #endregion

        #region Fire Event Helpers
        protected void WriteDiagnosticMessage(string fmt, params object[] objs)
        {
            var status = StatusUpdate;
            if (status != null)
                status(this, string.Format(fmt, objs));
        }

        protected void OnUserSubscribed(TwitchUser user)
        {
            var subscribed = UserSubscribed;
            if (subscribed != null)
                subscribed(this, user);
        }

        protected void OnActionReceived(IrcMessageEventArgs e)
        {
            var user = m_data.GetUser(e.Source.Name);
            var text = e.Text.Substring(m_action.Length, e.Text.Length - m_action.Length - 1);

            var evt = ActionReceived;
            if (evt != null)
                evt(this, user, text);
        }

        protected void OnMessageReceived(IrcMessageEventArgs e)
        {
            var user = m_data.GetUser(e.Source.Name);

            var msgRcv = MessageReceived;
            if (msgRcv != null)
                msgRcv(this, user, e.Text);
        }

        protected void OnInformSubscriber(TwitchUser user)
        {
            var evt = InformSubscriber;
            if (evt != null)
                evt(this, user);
        }

        protected void OnInformTurbo(string username)
        {
            var user = m_data.GetUser(username);

            var evt = InformTurbo;
            if (evt != null)
                evt(this, user);
        }
        protected void OnChatClear(string username)
        {
            var user = m_data.GetUser(username);

            var evt = InformChatClear;
            if (evt != null)
                evt(this, user);
        }
        #endregion


        #region Private Variables
        private ManualResetEventSlim m_joinedEvent = new ManualResetEventSlim(false);
        private ManualResetEventSlim m_connectedEvent = new ManualResetEventSlim(false);
        private ManualResetEventSlim m_registeredEvent = new ManualResetEventSlim(false);
        private IrcClient m_client;
        private string m_stream;
        private TwitchUsers m_data;
        private IrcChannel m_channel;
        private DateTime m_lastModCheck = DateTime.Now;
        private object m_modSync = new object();

        readonly string m_action = new string((char)1, 1) + "ACTION";
        FloodPreventer m_flood;
        bool m_loginFailed;

        int m_timeoutDelay;
        BlockingCollection<TimeoutRequest> m_timeouts = new BlockingCollection<TimeoutRequest>();
        volatile Thread m_thread;
        volatile bool m_shutdown;
        #endregion
    }

    class TimeoutRequest
    {
        TwitchClient m_client;
        string m_user;
        int m_time;

        public DateTime Created { get; private set; }

        public bool IsBan { get { return m_time < 0; } }

        public TimeoutRequest(TwitchClient client, string user, int timeout)
        {
            Created = DateTime.Now;
            m_client = client;
            m_user = user;
            m_time = timeout;
        }

        public void Execute()
        {
            if (IsBan)
                m_client.BanRaw(m_user);
            else
                m_client.TimeoutRaw(m_user, m_time);
        }
    }

    // 20 commands over 30 seconds = 8 hour ban for twitch irc
    // We'll limit to 15 messages to be safe.
    class FloodPreventer : IIrcFloodPreventer
    {
        object m_sync = new object();
        const int MessageLimit = 16;
        const int Timespan = 30;
        const int LowThreshold = 10;
        const int MediumThreshold = 5;

        LinkedList<DateTime> m_messages = new LinkedList<DateTime>();

        public event Action RejectedMessage;

        private TwitchClient m_client;
        public FloodPreventer(TwitchClient client)
        {
            m_client = client;
        }

        public bool ShouldSendMessage(Importance imp, string msg)
        {
            int remaining = GetRemaining();
            bool result;

            switch (imp)
            {
                case Importance.Low:
                    result = remaining >= LowThreshold;
                    break;

                case Importance.Med:
                    result = remaining >= MediumThreshold;
                    break;

                default:
                case Importance.High:
                    result = remaining > 0;
                    break;
            }

            if (!result)
                TwitchSource.Log.SoftMessageDrop(msg, (int)imp, remaining);

            return result;
        }

        public long GetSendDelay()
        {
            // IrcDotNet actually only cares if the value is 0, so we won't do a real msec calculation here.
            int remaining = GetRemaining();
            if (remaining <= 0)
            {
                var evt = RejectedMessage;
                if (evt != null)
                    evt();

                return 10000;
            }

            return 0;
        }

        public void HandleMessageSent()
        {
            lock (m_sync)
                m_messages.AddLast(DateTime.Now);
        }

        int GetRemaining()
        {
            lock (m_sync)
            {
                if (m_messages.Count == 0)
                    return MessageLimit;

                while (m_messages.Count > 0 && m_messages.First.Value.Elapsed().TotalSeconds >= Timespan)
                    m_messages.RemoveFirst();

                // IrcDotNet actually only cares if the value is 0, so we won't do a big calculation here.
                return MessageLimit - m_messages.Count;
            }
        }
    }
}
