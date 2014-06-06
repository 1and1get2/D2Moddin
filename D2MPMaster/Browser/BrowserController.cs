﻿using System;
using System.Linq;
using System.Text.RegularExpressions;
using ClientCommon.Methods;
using D2MPMaster.Browser.Methods;
using D2MPMaster.Client;
using D2MPMaster.Database;
using D2MPMaster.LiveData;
using D2MPMaster.Lobbies;
using D2MPMaster.Model;
using d2mpserver;
using MongoDB.Driver.Builders;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XSockets.Core.Common.Socket.Event.Arguments;
using XSockets.Core.Common.Socket.Event.Interface;
using XSockets.Core.XSocket;
using XSockets.Core.XSocket.Helpers;
using InstallMod = D2MPMaster.Browser.Methods.InstallMod;


namespace D2MPMaster.Browser
{
    public class BrowserController : XSocketController
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly ClientController ClientsController = new ClientController();

        public string ID;

        public BrowserController()
        {
            this.OnClose += OnClosed;
            ID = Utils.RandomString(10);
        }

        #region Variables
        //Chat flood prevention
        private string lastMsg = "";
        private DateTime lastMsgTime = DateTime.UtcNow;

        //User and lobby
        public User user = null;
        private Lobby _lobby = null;
        public Lobby lobby
        {
            get { return _lobby; }
            set
            {
                _lobby = value; 
                if (value != null)
                {
                    this.AsyncSendTo(m => m.user != null && m.user.Id == user.Id, ClearPublicLobbies(),
                        req => { });
                }
                else
                {
                    this.AsyncSendTo(m => m.user != null && m.user.Id == user.Id, PublicLobbySnapshot(),
                        req => { });
                    this.AsyncSendTo(m => m.user != null && m.user.Id == user.Id, ClearLobbyR(),
                        req => { });
                }
            }
        }

        #endregion

        #region Helpers
        public static void CheckLobby(BrowserController controller)
        {
            if (controller.lobby != null && controller.lobby.deleted) controller.lobby = null;
        }

        public void RespondError(JObject req, string msg)
        {
            JObject resp = new JObject();
            resp["msg"] = "error";
            resp["reason"] = msg;
            resp["req"] = req["id"];
            Send(resp.ToString(Formatting.None));
        }

        #endregion

        #region Message Handling
        public override void OnMessage(ITextArgs args)
        {
            try
            {
                var jdata = JObject.Parse(args.data);
                var id = jdata["id"];
                if (id == null) return;
                var command = id.Value<string>();
                switch (command)
                {
                    #region Authentication

                    case "deauth":
                        user = null;
                        this.SendJson("{\"status\": false}", "auth");
                        break;
                    case "auth":
                        //Parse the UID
                        var uid = jdata["uid"].Value<string>();
                        if (user != null && uid == user.Id)
                        {
                            this.SendJson("{\"msg\": \"auth\", \"status\": true}", "auth");
                            return;
                        }
                        //Parse the resume key
                        var key = jdata["key"]["hashedToken"].Value<string>();
                        //Find it in the database
                        var usr = Mongo.Users.FindOneAs<User>(Query.EQ("_id", uid));
                        bool tokenfound = false;
                        if (usr != null)
                        {
                            var tokens = usr.services.resume.loginTokens;
                            tokenfound = tokens.Any(token => token.hashedToken == key);
                        }
                        if (tokenfound && usr.status.online)
                        {
                            if (usr.authItems != null && usr.authItems.Contains("banned"))
                            {
                                log.Debug(string.Format("User is banned {0}", usr.profile.name));
                                RespondError(jdata, "You are banned from the lobby server.");
                                this.SendJson("{\"msg\": \"auth\", \"status\": false}", "auth");
                                return;
                            }  
                            user = usr;
                            this.SendJson("{\"msg\": \"auth\", \"status\": true}", "auth");
                            this.Send(PublicLobbySnapshot());
                        }
                        else
                        {                            
                            user = null;
                            this.SendJson("{\"msg\": \"auth\", \"status\": false}", "auth");
                        }
                        break;

                    #endregion

                    case "createlobby":
                        {
                            if (user == null)
                            {
                                RespondError(jdata, "You are not logged in!");
                                return;
                            }
                            //Check if they are in a lobby
                            //CheckLobby();
                            if (lobby != null)
                            {
                                RespondError(jdata, "You are already in a lobby.");
                                return; //omfg
                            }
                            //Parse the create lobby request
                            var req = jdata["req"].ToObject<CreateLobby>();
                            if (req.name == null)
                            {
                                req.name = user.profile.name + "'s Lobby";
                            }
                            if (req.mod == null)
                            {
                                RespondError(jdata, "You did not specify a mod.");
                                return;
                            }
                            //Find the mod
                            var mod = Mods.Mods.ByID(req.mod);
                            if (mod == null)
                            {
                                RespondError(jdata, "Can't find the mod, you probably don't have access.");
                                return;
                            }
                            //Find the client
                            var clients = ClientsController.Find(m => m.UID == user.Id);
                            if (!clients.Any(m => m.Mods.Any(c => c.name == mod.name && c.version == mod.version)))
                            {
                                var obj = new JObject();
                                obj["msg"] = "modneeded";
                                obj["name"] = mod.name;
                                Send(obj.ToString(Formatting.None));
                                return;
                            }

                            lobby = LobbyManager.CreateLobby(user, mod, req.name);
                            break;
                        }
                    case "switchteam":
                        {
                            if (user == null)
                            {
                                RespondError(jdata, "You are not logged in.");
                                return;
                            }
                            if (lobby == null)
                            {
                                RespondError(jdata, "You are not in a lobby.");
                                return;
                            }
                            //Parse the switch team request
                            var req = jdata["req"].ToObject<SwitchTeam>();
                            var goodguys = req.team == "radiant";
                            if ((goodguys && lobby.TeamCount(lobby.radiant) >= 5) ||
                                (!goodguys && lobby.TeamCount(lobby.dire) >= 5))
                            {
                                RespondError(jdata, "That team is full.");
                                return;
                            }
                            LobbyManager.RemoveFromTeam(lobby, user.services.steam.steamid);
                            lobby.AddPlayer(goodguys ? lobby.radiant : lobby.dire, Player.FromUser(user));
                            LobbyManager.TransmitLobbyUpdate(lobby, new[] { "radiant", "dire" });
                            break;
                        }
                    case "leavelobby":
                        {
                            if (user == null)
                            {
                                RespondError(jdata, "You are not logged in.");
                                return;
                            }
                            if (lobby == null)
                            {
                                RespondError(jdata, "You are not in a lobby.");
                                return;
                            }
                            LobbyManager.LeaveLobby(this);
                            break;
                        }
                    case "chatmsg":
                        {
                            if (user == null)
                            {
                                RespondError(jdata, "You are not logged in (can't chat).");
                                return;
                            }
                            if (lobby == null)
                            {
                                RespondError(jdata, "You are not in a lobby (can't chat).");
                                return;
                            }
                            var req = jdata["req"].ToObject<ChatMessage>();
                            var msg = req.message;
                            if (msg == null) return;
                            msg = Regex.Replace(msg, "^[\\w \\.\"'[]\\{\\}\\(\\)]+", "");
                            if (msg == lastMsg)
                            {
                                RespondError(jdata, "You cannot send the same message twice in a row.");
                                return;
                            }
                            var now = DateTime.UtcNow;
                            TimeSpan span = now - lastMsgTime;
                            if (span.TotalSeconds < 2)
                            {
                                RespondError(jdata, "You must wait 2 seconds between each message.");
                                return;
                            }
                            lastMsg = msg;
                            lastMsgTime = now;
                            LobbyManager.ChatMessage(lobby, msg, user.profile.name);
                            break;
                        }
                    case "kickplayer":
                        {
                            if (user == null)
                            {
                                RespondError(jdata, "You are not logged in (can't kick).");
                                return;
                            }
                            if (lobby == null)
                            {
                                RespondError(jdata, "You are not in a lobby (can't kick).");
                                return;
                            }
                            if (lobby.creatorid != user.Id)
                            {
                                RespondError(jdata, "You are not the lobby host.");
                                return;
                            }
                            var req = jdata["req"].ToObject<KickPlayer>();
                            LobbyManager.BanFromLobby(lobby, req.steam);
                            break;
                        }
                    case "setname":
                        {
                            if (user == null)
                            {
                                RespondError(jdata, "You are not logged in (can't set name).");
                                return;
                            }
                            if (lobby == null)
                            {
                                RespondError(jdata, "You are not in a lobby (can't set name).");
                                return;
                            }
                            if (lobby.creatorid != user.Id)
                            {
                                RespondError(jdata, "You are not the lobby host.");
                                return;
                            }

                            var req = jdata["req"].ToObject<SetName>();
                            var err = req.Validate();
                            if (err != null)
                            {
                                RespondError(jdata, err);
                                return;
                            }
                            LobbyManager.SetTitle(lobby, req.name);
                            break;
                        }
                    case "setregion":
                        {
                            if (user == null)
                            {
                                RespondError(jdata, "You are not logged in (can't set region).");
                                return;
                            }
                            if (lobby == null)
                            {
                                RespondError(jdata, "You are not in a lobby (can't set region).");
                                return;
                            }
                            if (lobby.creatorid != user.Id)
                            {
                                RespondError(jdata, "You are not the lobby host.");
                                return;
                            }
                            var req = jdata["req"].ToObject<SetRegion>();
                            LobbyManager.SetRegion(lobby, req.region);
                            break;
                        }
                    case "startqueue":
                        {
                            if (user == null)
                            {
                                RespondError(jdata, "You are not logged in yet.");
                                return;
                            }
                            if (lobby == null)
                            {
                                RespondError(jdata, "You are not in a lobby.");
                                return;
                            }
                            if (lobby.creatorid != user.Id)
                            {
                                RespondError(jdata, "You are not the lobby host.");
                                return;
                            }
                            if (lobby.status != LobbyStatus.Start)
                            {
                                RespondError(jdata, "You are already queuing/playing.");
                                return;
                            }
                            if (lobby.requiresFullLobby &&
                                (lobby.TeamCount(lobby.dire) + lobby.TeamCount(lobby.radiant) < 10))
                            {
                                RespondError(jdata, "Your lobby must be full to start.");
                                return;
                            }
                            LobbyManager.StartQueue(lobby);
                            return;
                        }
                    case "stopqueue":
                        {
                            if (user == null)
                            {
                                RespondError(jdata, "You are not logged in yet.");
                                return;
                            }
                            if (lobby == null)
                            {
                                RespondError(jdata, "You are not in a lobby.");
                                return;
                            }
                            if (lobby.creatorid != user.Id)
                            {
                                RespondError(jdata, "You are not the lobby host.");
                                return;
                            }
                            if (lobby.status != LobbyStatus.Queue)
                            {
                                RespondError(jdata, "You are not queueing.");
                                return;
                            }
                            LobbyManager.CancelQueue(lobby);
                            return;
                        }
                    case "joinlobby":
                        {
                            if (user == null)
                            {
                                RespondError(jdata, "You are not logged in yet.");
                                return;
                            }
                            if (lobby != null)
                            {
                                RespondError(jdata, "You are already in a lobby.");
                                return;
                            }
                            var req = jdata["req"].ToObject<JoinLobby>();
                            //Find lobby
                            var lob = LobbyManager.PublicLobbies.FirstOrDefault(m => m.id == req.LobbyID);
                            if (lob == null)
                            {
                                RespondError(jdata, "Can't find that lobby.");
                                return;
                            }
                            if (lob.TeamCount(lob.dire) >= 5 && lob.TeamCount(lob.radiant) >= 5)
                            {
                                RespondError(jdata, "That lobby is full.");
                                return;
                            }
                            if (lob.banned.Contains(user.services.steam.steamid))
                            {
                                RespondError(jdata, "You are banned from that lobby.");
                                return;
                            }
                            //Find the mod
                            var mod = Mods.Mods.ByID(lob.mod);
                            if (mod == null)
                            {
                                RespondError(jdata, "Can't find the mod, you probably don't have access.");
                                return;
                            }
                            //Find the client
                            var clients = ClientsController.Find(m => m.UID == user.Id);
                            if (!clients.Any(m => m.Mods.Any(c => c.name == mod.name && c.version == mod.version)))
                            {
                                var obj = new JObject();
                                obj["msg"] = "modneeded";
                                obj["name"] = mod.name;
                                Send(obj.ToString(Formatting.None));
                                return;
                            }

                            LobbyManager.JoinLobby(lob, user, this);
                            break;
                        }
                    case "installmod":
                        {
                            if (user == null)
                            {
                                RespondError(jdata, "You are not logged in.");
                                return;
                            }
                            var clients = ClientsController.Find(m => m.UID == user.Id);
                            var clientControllers = clients as ClientController[] ?? clients.ToArray();
                            if (!clientControllers.Any())
                            {
                                //Error message
                                RespondError(jdata, "Your client has not been started yet.");
                                return;
                            }
                            var req = jdata["req"].ToObject<InstallMod>();
                            var mod = Mods.Mods.ByName(req.mod);
                            if (mod == null)
                            {
                                RespondError(jdata, "Can't find that mod in the database.");
                                return;
                            }
                            if (clientControllers.FirstOrDefault().Mods.Any(m => m.name == mod.name && m.version == mod.version))
                            {
                                this.AsyncSendTo(x => x.user != null && x.user.Id == user.Id, InstallResponse("The mod has already been installed.", true),
                                    rf => { });
                                return;
                            }

                            ClientsController.AsyncSendTo(m => m.UID == user.Id, ClientController.InstallMod(mod),
                                rf => { });
                            break;
                        }
                    case "connectgame":
                        {
                            if (user == null)
                            {
                                RespondError(jdata, "You are not logged in yet.");
                                return;
                            }
                            if (lobby == null)
                            {
                                RespondError(jdata, "You are not in a lobby.");
                                return;
                            }
                            if (lobby.status != LobbyStatus.Play)
                            {
                                RespondError(jdata, "Your lobby isn't ready to play yet.");
                                return;
                            }
                            LobbyManager.LaunchAndConnect(lobby, user.services.steam.steamid);
                            break;
                        }
                    default:
                        log.Debug(string.Format("Unknown command: {0}...", command.Substring(0, 10)));
                        return;
                }
            }
            catch (Exception ex)
            {
                log.Error(ex.ToString());
            } //Handle all malformed JSON / no ID field / other troll data
        }
        #endregion

        public void Send(string msg)
        {
            this.SendJson(msg, "lobby");
        }

        public void OnClosed(object sender, OnClientDisconnectArgs e)
        {
            if (user == null) return;
            if (lobby != null && !this.Find(p => p.user != null && p.user.Id == user.Id).Any())
            {
                LobbyManager.LeaveLobby(this);
            }
        }


        public static ITextArgs ClearLobbyR()
        {
            var upd = new JObject();
            upd["msg"] = "colupd";
            upd["ops"] = new JArray { DiffGenerator.RemoveAll("lobbies") };
            var msg = upd.ToString(Formatting.None);
            return new TextArgs(msg, "lobby");
        }

        public static ITextArgs LobbySnapshot(Lobby lobby1)
        {
            var upd = new JObject();
            upd["msg"] = "colupd";
            upd["ops"] = new JArray { DiffGenerator.RemoveAll("lobbies"), lobby1.Add("lobbies") };
            return new TextArgs(upd.ToString(Formatting.None), "lobby");
        }

        public static ITextArgs PublicLobbySnapshot()
        {
            var upd = new JObject();
            var ops = new JArray { DiffGenerator.RemoveAll("publicLobbies") };
            foreach (var lobby in LobbyManager.PublicLobbies)
            {
                ops.Add(lobby.Add("publicLobbies"));
            }
            upd["msg"] = "colupd";
            upd["ops"] = ops;
            return new TextArgs(upd.ToString(Formatting.None), "lobby");
        }

        public static ITextArgs ClearPublicLobbies()
        {
            var upd = new JObject();
            var ops = new JArray { DiffGenerator.RemoveAll("publicLobbies") };
            upd["msg"] = "colupd";
            upd["ops"] = ops;
            return new TextArgs(upd.ToString(Formatting.None), "lobby");
        }

        public static ITextArgs ChatMessage(string cmsg)
        {
            var cmd = new JObject();
            cmd["msg"] = "chat";
            cmd["message"] = cmsg;
            var data = cmd.ToString(Formatting.None);
            return new TextArgs(data, "lobby");
        }

        public static ITextArgs InstallResponse(string message, bool success)
        {
            var upd = new JObject();
            upd["msg"] = "installres";
            upd["success"] = success;
            upd["message"] = message;
            return new TextArgs(upd.ToString(Formatting.None), "lobby");
        }
    }
}