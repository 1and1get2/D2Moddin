﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ClientCommon.Data;
using ClientCommon.Methods;
using D2MPMaster.Browser;
using D2MPMaster.Database;
using D2MPMaster.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XSockets.Core.Common.Socket.Event.Arguments;
using XSockets.Core.Common.Socket.Event.Interface;
using XSockets.Core.XSocket;
using XSockets.Core.XSocket.Helpers;
using Query = MongoDB.Driver.Builders.Query;
using Version = ClientCommon.Version;
using System.Collections.Generic;

namespace D2MPMaster.Client
{
    public class ClientController : XSocketController
    {
        private static readonly BrowserController Browser = new BrowserController();
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        public ObservableCollection<ClientMod> Mods = new ObservableCollection<ClientMod>();
        public Init InitData;
        public string UID;
        public string SteamID;
        public bool Inited { get; set; }

        private object MsgLock = new object();

        public ClientController()
        {
            this.OnOpen += OnClientConnect;
            this.OnClose += DeregisterClient;
            this.OnError += OnClientError;
        }

        private void OnClientConnect(object sender, OnClientConnectArgs e)
        {
        }

        private void OnClientError(object sender, OnErrorArgs args)
        {
            log.Error(args.Message, args.Exception);
        }

        void DeregisterClient(object se, OnClientDisconnectArgs e)
        {
            if (UID == null) return;
            var browsers = Browser.Find(m => m.user != null && m.user.Id == UID);
            foreach (var browser in browsers)
            {
                browser.SendManagerStatus(false);
            }
        }

		void RegisterClient()
		{
			//Figure out UID
			var users = new List<User>();
			foreach (var steamid in InitData.SteamIDs.Where(steamid => steamid.Length == 17))
			{
				var user = Mongo.Users.FindOneAs<User>(Query.EQ("steam.steamid", steamid));
				if (user != null) users.Add(user);
			}

			if (users.Count == 0)
			{
                this.AsyncSend(NotifyMessage("No registered account found","No D2Moddin account found for your active Steam account. Please login to Steam using your registered D2Moddin account and restart the client.", true) , ar => { });
                log.Debug("Can't find any users for client.");
				return;
			}
			SteamID = users.FirstOrDefault ().steam.steamid;
			UID = users.FirstOrDefault ().Id;

			/*
			var tbrowser = users.Select(user => Browser.Find(m => m.user != null && m.user.Id == user.Id).FirstOrDefault()).FirstOrDefault(browser => browser != null);

			if (tbrowser != null)
				UID = tbrowser.user.Id;
			else
			{
				var usr = users.FirstOrDefault();
				if (usr != null) UID = usr.Id;
			}*/

			Inited = true;

			//Find if the user is online
			var browsersn = Browser.Find(e => e.user != null && e.user.Id == UID);
			foreach (var browser in browsersn)
			{
				browser.SendManagerStatus(true);
			}
		}

        public static ITextArgs InstallMod(Mod mod)
        {
            var msg = JObject.FromObject(new InstallMod() { Mod = mod.ToClientMod(), url = Program.S3.GenerateModURL(mod) }).ToString(Formatting.None);
            return new TextArgs(msg, "commands");
        }

        public static ITextArgs LaunchDota()
        {
            return new TextArgs(JObject.FromObject(new LaunchDota()).ToString(Formatting.None), "commands");
        }

        public static ITextArgs NotifyMessage(string title, string message)
        {
            return new TextArgs(JObject.FromObject(new NotifyMessage() { message = new Message() { title = title, message = message } }).ToString(Formatting.None), "commands");
        }

        public static ITextArgs NotifyMessage(string title, string message, bool shutdown)
        {
            return new TextArgs(JObject.FromObject(new NotifyMessage() { message = new Message() { title = title, message = message, shutdown = shutdown } }).ToString(Formatting.None), "commands");
        }

        public static ITextArgs SetMod(Mod mod)
        {
            var msg = JObject.FromObject(new SetMod() { Mod = mod.ToClientMod() }).ToString(Formatting.None);
            return new TextArgs(msg, "commands");
        }

        public override void OnMessage(ITextArgs textArgs)
        {
            try
            {
                var jdata = JObject.Parse(textArgs.data);
                var id = jdata["msg"];
                if (id == null) return;
                var command = id.Value<string>();
                Task.Factory.StartNew(() =>
                {
                    lock (MsgLock)
                    {
                        try
                        {
                            switch (command)
                            {
                                case OnInstalledMod.Msg:
                                {
                                    var msg = jdata.ToObject<OnInstalledMod>();
                                    log.Debug(SteamID + " -> installed " + msg.Mod.name + ".");
                                    Mods.Add(msg.Mod);
                                    Browser.AsyncSendTo(x => x.user != null && x.user.steam.steamid == SteamID,
                                        BrowserController.InstallResponse("The mod has been installed.", true),
                                        rf => { });
                                    break;
                                }
                                case OnDeletedMod.Msg:
                                {
                                    var msg = jdata.ToObject<OnDeletedMod>();
                                    log.Debug(SteamID + " -> removed " + msg.Mod.name + ".");
                                    var localMod = Mods.FirstOrDefault(m => Equals(msg.Mod, m));
                                    if (localMod != null) Mods.Remove(localMod);
                                    break;
                                }
                                case Init.Msg:
                                {
                                    var msg = jdata.ToObject<Init>();
                                    InitData = msg;
                                    if (msg.Version != Version.ClientVersion)
                                    {
                                        this.SendJson(JObject.FromObject(new Shutdown()).ToString(Formatting.None),
                                            "commands");
                                        return;
                                    }
                                    foreach (var mod in msg.Mods.Where(mod => mod.name != null && mod.version != null))
                                        Mods.Add(mod);
                                    //Insert the client into the DB
                                    RegisterClient();
                                    break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                //log.Error("Parsing client message.", ex);
            }
        }

        public static ITextArgs ConnectDota(string serverIp)
        {
            return new TextArgs(JObject.FromObject(new ConnectDota() { ip = serverIp }).ToString(Formatting.None), "commands");
        }

        public static ITextArgs Shutdown()
        {
            return new TextArgs(JObject.FromObject(new ClientCommon.Methods.Shutdown()).ToString(), "commands");
        }
        
        public static ITextArgs Uninstall()
        {
            return new TextArgs(JObject.FromObject(new ClientCommon.Methods.Uninstall()).ToString(), "commands");
        }
    }
}
