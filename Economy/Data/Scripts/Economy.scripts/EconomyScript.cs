﻿/*
 *  Economy Mod V1.0A 
 *  by PhoenixX (JPC Dev), Tangentspy, Screaming Angels
 *  For use with Space Engineers Game
 *  Refer to github issues or steam/git dev guide/wiki or the team notes
 *  for direction what needs to be worked on next
*/

namespace Economy.scripts
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using System.Timers;
    using Economy.scripts.EconConfig;
    using Economy.scripts.EconStructures;
    using Economy.scripts.Management;
    using Economy.scripts.Messages;
    using Sandbox.Common;
    using Sandbox.Common.Components;
    using Sandbox.Common.ObjectBuilders;
    using Sandbox.Definitions;
    using Sandbox.ModAPI;
    using Sandbox.ModAPI.Interfaces;
    using VRage;
    using VRage.ModAPI;
    using VRage.ObjectBuilders;
    using VRageMath;

    [Sandbox.Common.MySessionComponentDescriptor(Sandbox.Common.MyUpdateOrder.AfterSimulation)]
    public class EconomyScript : MySessionComponentBase
    {
        #region constants

        const string PayPattern = @"(?<command>/pay)\s+(?:(?:""(?<user>[^""]|.*?)"")|(?<user>[^\s]*))\s+(?<value>[+-]?((\d+(\.\d*)?)|(\.\d+)))\s+(?<reason>.+)\s*$";
        const string BalPattern = @"(?<command>/bal)(?:\s+(?:(?:""(?<user>[^""]|.*?)"")|(?<user>[^\s]*)))?";
        const string SeenPattern = @"(?<command>/seen)\s+(?:(?:""(?<user>[^""]|.*?)"")|(?<user>[^\s]*))";
        const string ValuePattern = @"(?<command>/value)\s+(?:(?<Key>.+)\s+(?<Value>[+-]?((\d+(\.\d*)?)|(\.\d+)))|(?<Key>.+))";

        /// <summary>
        /// sell pattern check for "/sell" at[0], then number or string at [1], then string at [2], then number at [3], then string at [4]
        /// samples(optional): /sell all iron (price) (player/faction) || /sell 10 iron (price) (player/faction) || /sell accept || /sell deny || /sell cancel
        /// </summary>
        const string SellPattern = @"(?<command>/sell)\s+(?:(?<qty>[+-]?((\d+(\.\d*)?)|(\.\d+)))|(?<qtyall>ALL))\s+(?:(?:""(?<item>[^""]|.*?)"")|(?<item>.*(?=\s+\d+\b))|(?<item>.*$))(?:\s+(?<price>[+-]?((\d+(\.\d*)?)|(\.\d+)))(?:\s+(?:(?:""(?<user>[^""]|.*?)"")|(?<user>[^\s]*)))?)?";

        //set is for admins to configure things like on hand, npc market pricing, toggle blacklist, turn on limited or unlimited stock mode
        //set their currency name, player default starting balance, enable or diable limited range mode, and set default trade range
        //at the moment all it does is set the on hand figures -
        //possible examples /set limitedsupply true,  /set limitedrange true, /set 1000 metal, /set starting 200, /set blacklist organic false, /set traderange 1000, /set currency credits
        // /set reset etc  the syntax may not be exactly as above these are just hypothetical examples
        // see https://github.com/jpcsupplies/Economy_mod/issues/66
        //current functionality is just to alter the market on hand of a single item, eg /set 10000 ice
        //or /set blacklist ice   or /set buy item price

        const string SetPattern = @"(?<command>/set)\s+(?:(?<qty>[+-]?((\d+(\.\d*)?)|(\.\d+)))|(?<blacklist>blacklist)|(?<tradezone>tradezone)|(?<buy>buy)|(?<sell>sell))\s+(?:(?:""(?<item>[^""]|.*?)"")|(?<item>.*(?=\s+\d+\b))|(?<item>.*$))(?:\s+(?<price>[+-]?((\d+(\.\d*)?)|(\.\d+))))?";

        /// <summary>
        ///  buy pattern no "all" required.   reusing sell  
        /// /buy 10 "iron ingot" || /buy 10 "iron ingot" 1 || /buy 10 "iron ingot" 1 fred
        /// </summary>
        const string BuyPattern = @"(?<command>/buy)\s+(?<qty>[+-]?((\d+(\.\d*)?)|(\.\d+)))\s+(?:(?:""(?<item>[^""]|.*?)"")|(?<item>[^\s]*))(?:\s+(?<price>[+-]?((\d+(\.\d*)?)|(\.\d+)))(?:\s+(?:(?:""(?<user>[^""]|.*?)"")|(?<user>[^\s]*)))?)?";

        #endregion

        #region fields

        private bool _isInitialized;
        private bool _isClientRegistered;
        private bool _isServerRegistered;
        private bool _delayedConnectionRequest;
        private Timer _timer1Events; // 1 second.
        private Timer _timer10Events; // 10 seconds.
        private Timer _timer3600Events; // 1 hour.
        private bool _timer1Block;
        private bool _timer10Block;
        private bool _timer3600Block;

        private readonly Action<byte[]> _messageHandler = new Action<byte[]>(HandleMessage);

        public static EconomyScript Instance;

        public TextLogger ServerLogger = new TextLogger(); // This is a dummy logger until Init() is called.
        public TextLogger ClientLogger = new TextLogger(); // This is a dummy logger until Init() is called.
        public Timer DelayedConnectionRequestTimer;

        /// Ideally this data should be persistent until someone buys/sells/pays/joins but
        /// lacking other options it will triggers read on these events instead. bal/buy/sell/pay/join
        public EconDataStruct Data;
        public EconConfigStruct Config;

        /// <summary>
        /// Set manually to true for testing purposes. No need for this function in general.
        /// </summary>
        public bool DebugOn = false;

        public static CultureInfo ServerCulture;

        #endregion

        #region attaching events and wiring up

        public override void UpdateAfterSimulation()
        {
            Instance = this;

            // This needs to wait until the MyAPIGateway.Session.Player is created, as running on a Dedicated server can cause issues.
            // It would be nicer to just read a property that indicates this is a dedicated server, and simply return.
            if (!_isInitialized && MyAPIGateway.Session != null && MyAPIGateway.Session.Player != null)
            {
                DebugOn = MyAPIGateway.Session.Player.IsExperimentalCreator();
                if (MyAPIGateway.Session.OnlineMode.Equals(MyOnlineModeEnum.OFFLINE)) // pretend single player instance is also server.
                    InitServer();
                if (!MyAPIGateway.Session.OnlineMode.Equals(MyOnlineModeEnum.OFFLINE) && MyAPIGateway.Multiplayer.IsServer && !MyAPIGateway.Utilities.IsDedicated)
                    InitServer();
                InitClient();
            }

            // Dedicated Server.
            if (!_isInitialized && MyAPIGateway.Utilities != null && MyAPIGateway.Multiplayer != null
                && MyAPIGateway.Session != null && MyAPIGateway.Utilities.IsDedicated && MyAPIGateway.Multiplayer.IsServer)
            {
                InitServer();
                return;
            }

            if (_delayedConnectionRequest)
            {
                ClientLogger.WriteInfo("Delayed Connection Request");
                _delayedConnectionRequest = false;
                MessageConnectionRequest.SendMessage(EconomyConsts.ModCommunicationVersion);
            }

            base.UpdateAfterSimulation();
        }

        private void InitClient()
        {
            _isInitialized = true; // Set this first to block any other calls from UpdateAfterSimulation().
            _isClientRegistered = true;

            ClientLogger.Init("EconomyClient.Log", false, 20); // comment this out if logging is not required for the Client.
            ClientLogger.WriteStart("Economy Client Log Started");
            ClientLogger.WriteInfo("Economy Client Version {0}", EconomyConsts.ModCommunicationVersion);
            if (ClientLogger.IsActive)
                VRage.Utils.MyLog.Default.WriteLine(String.Format("##Mod## Economy Client Logging File: {0}", ClientLogger.LogFile));

            MyAPIGateway.Utilities.MessageEntered += GotMessage;

            if (MyAPIGateway.Multiplayer.MultiplayerActive && !_isServerRegistered) // if not the server, also need to register the messagehandler.
            {
                ClientLogger.WriteStart("RegisterMessageHandler");
                MyAPIGateway.Multiplayer.RegisterMessageHandler(EconomyConsts.ConnectionId, _messageHandler);
            }

            DelayedConnectionRequestTimer = new Timer(10000);
            DelayedConnectionRequestTimer.Elapsed += DelayedConnectionRequestTimer_Elapsed;
            DelayedConnectionRequestTimer.Start();

            // let the server know we are ready for connections
            MessageConnectionRequest.SendMessage(EconomyConsts.ModCommunicationVersion);
        }

        private void InitServer()
        {
            _isInitialized = true; // Set this first to block any other calls from UpdateAfterSimulation().
            _isServerRegistered = true;
            ServerLogger.Init("EconomyServer.Log", false, 20); // comment this out if logging is not required for the Server.
            ServerLogger.WriteStart("Economy Server Log Started");
            ServerLogger.WriteInfo("Economy Server Version {0}", EconomyConsts.ModCommunicationVersion);
            if (ServerLogger.IsActive)
                VRage.Utils.MyLog.Default.WriteLine(String.Format("##Mod## Economy Server Logging File: {0}", ServerLogger.LogFile));

            ServerLogger.WriteStart("RegisterMessageHandler");
            MyAPIGateway.Multiplayer.RegisterMessageHandler(EconomyConsts.ConnectionId, _messageHandler);

            ServerLogger.WriteStart("LoadBankContent");

            Config = EconDataManager.LoadConfig(); // Load config first.
            Data = EconDataManager.LoadData(Config.DefaultPrices);

            SetLanguage();

            // start the timer last, as all data should be loaded before this point.
            // TODO: use a single timer, and a counter.
            ServerLogger.WriteStart("Attaching Event 1 timer.");
            _timer1Events = new Timer(1000);
            _timer1Events.Elapsed += Timer1EventsOnElapsed;
            _timer1Events.Start();

            ServerLogger.WriteStart("Attaching Event 10 timer.");
            _timer10Events = new Timer(10000);
            _timer10Events.Elapsed += Timer10EventsOnElapsed;
            _timer10Events.Start();

            ServerLogger.WriteStart("Attaching Event 3600 timer.");
            _timer3600Events = new Timer(3600000);
            _timer3600Events.Elapsed += Timer3600EventsOnElapsed;
            _timer3600Events.Start();
        }

        #endregion

        #region detaching events

        protected override void UnloadData()
        {
            if (_isClientRegistered)
            {
                if (MyAPIGateway.Utilities != null)
                {
                    MyAPIGateway.Utilities.MessageEntered -= GotMessage;
                }

                if (!_isServerRegistered) // if not the server, also need to unregister the messagehandler.
                {
                    ClientLogger.WriteStop("UnregisterMessageHandler");
                    MyAPIGateway.Multiplayer.UnregisterMessageHandler(EconomyConsts.ConnectionId, _messageHandler);
                }

                if (DelayedConnectionRequestTimer != null)
                {
                    DelayedConnectionRequestTimer.Stop();
                    DelayedConnectionRequestTimer.Close();
                }

                ClientLogger.WriteStop("Log Closed");
                ClientLogger.Terminate();
            }

            if (_isServerRegistered)
            {
                ServerLogger.WriteStop("UnregisterMessageHandler");
                MyAPIGateway.Multiplayer.UnregisterMessageHandler(EconomyConsts.ConnectionId, _messageHandler);

                if (_timer1Events != null)
                {
                    ServerLogger.WriteStop("Stopping Event 1 timer.");
                    _timer1Events.Stop();
                    _timer1Events.Elapsed -= Timer1EventsOnElapsed;
                    _timer1Events = null;
                }

                if (_timer10Events != null)
                {
                    ServerLogger.WriteStop("Stopping Event 10 timer.");
                    _timer10Events.Stop();
                    _timer10Events.Elapsed -= Timer10EventsOnElapsed;
                    _timer10Events = null;
                }

                if (_timer3600Events != null)
                {
                    ServerLogger.WriteStop("Stopping Event 3600 timer.");
                    _timer3600Events.Stop();
                    _timer3600Events.Elapsed -= Timer3600EventsOnElapsed;
                    _timer3600Events = null;
                }


                Data = null;

                ServerLogger.WriteStop("Log Closed");
                ServerLogger.Terminate();
            }

            base.UnloadData();
        }

        public override void SaveData()
        {
            base.SaveData();

            if (_isServerRegistered)
            {
                if (Data != null)
                {
                    ServerLogger.WriteInfo("Save Data");
                    EconDataManager.SaveData(Data);
                }

                if (Config != null)
                {
                    ServerLogger.WriteInfo("Save Config");
                    EconDataManager.SaveConfig(Config);
                }
            }
        }

        #endregion

        #region message handling

        private void GotMessage(string messageText, ref bool sendToOthers)
        {
            try
            {
                // here is where we nail the echo back on commands "return" also exits us from processMessage
                if (ProcessMessage(messageText)) { sendToOthers = false; }
            }
            catch (Exception ex)
            {
                ClientLogger.WriteException(ex);
                MyAPIGateway.Utilities.ShowMessage("Error", "An exception has been logged in the file: {0}", ClientLogger.LogFileName);
            }
        }

        private static void HandleMessage(byte[] message)
        {
            EconomyScript.Instance.ServerLogger.WriteVerbose("HandleMessage");
            EconomyScript.Instance.ClientLogger.WriteVerbose("HandleMessage");
            ConnectionHelper.ProcessData(message);
        }

        void DelayedConnectionRequestTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            _delayedConnectionRequest = true;
        }

        private void Timer1EventsOnElapsed(object sender, ElapsedEventArgs elapsedEventArgs)
        {
            // DO NOT SET ANY IN GAME API CALLS HERE. AT ALL!
            MyAPIGateway.Utilities.InvokeOnGameThread(delegate
            {
                // Recheck main Gateway properties, as the Game world my be currently shutting down when the InvokeOnGameThread is called.
                if (MyAPIGateway.Players == null || MyAPIGateway.Entities == null || MyAPIGateway.Session == null || MyAPIGateway.Utilities == null)
                    return;

                if (_timer1Block) // prevent other any additional calls into this code while it may still be running.
                    return;

                _timer1Block = true;
                try
                {
                    // Any processing needs to occur in here, as it will be on the main thread, and hopefully thread safe.
                    LcdManager.UpdateLcds();
                }
                finally
                {
                    _timer1Block = false;
                }
            });
        }

        private void Timer10EventsOnElapsed(object sender, ElapsedEventArgs elapsedEventArgs)
        {
            // DO NOT SET ANY IN GAME API CALLS HERE. AT ALL!
            MyAPIGateway.Utilities.InvokeOnGameThread(delegate
            {
                // Recheck main Gateway properties, as the Game world my be currently shutting down when the InvokeOnGameThread is called.
                if (MyAPIGateway.Players == null || MyAPIGateway.Entities == null || MyAPIGateway.Session == null || MyAPIGateway.Utilities == null)
                    return;

                if (_timer10Block) // prevent other any additional calls into this code while it may still be running.
                    return;

                _timer10Block = true;
                try
                {
                    // Any processing needs to occur in here, as it will be on the main thread, and hopefully thread safe.
                    MarketManager.CheckTradeTimeouts();
                }
                finally
                {
                    _timer10Block = false;
                }
            });
        }

        private void Timer3600EventsOnElapsed(object sender, ElapsedEventArgs elapsedEventArgs)
        {
            // DO NOT SET ANY IN GAME API CALLS HERE. AT ALL!
            MyAPIGateway.Utilities.InvokeOnGameThread(delegate
            {
                // Recheck main Gateway properties, as the Game world my be currently shutting down when the InvokeOnGameThread is called.
                if (MyAPIGateway.Players == null || MyAPIGateway.Entities == null || MyAPIGateway.Session == null || MyAPIGateway.Utilities == null)
                    return;

                if (_timer3600Block) // prevent other any additional calls into this code while it may still be running.
                    return;

                _timer3600Block = true;
                try
                {
                    // Any processing needs to occur in here, as it will be on the main thread, and hopefully thread safe.
                    // TODO: hourly market processing.
                }
                finally
                {
                    _timer3600Block = false;
                }
            });
        }

        #endregion

        #region command list
        private bool ProcessMessage(string messageText)
        {
            Match match; // used by the Regular Expression to test user input.
                         // this list is going to get messy since the help and commands themself tell user the same thing 
            string[] split = messageText.Split(new Char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            // nothing useful was entered.
            if (split.Length == 0)
                return false;

            #region pay
            // pay command
            // eg /pay bob 50 here is your payment
            // eg /pay "Screaming Angles" 10 fish and chips
            if (split[0].Equals("/pay", StringComparison.InvariantCultureIgnoreCase))
            {   //might need to add a check here preventing normal players paying NPC but not critical since they would be silly to try
                match = Regex.Match(messageText, PayPattern, RegexOptions.IgnoreCase);
                if (match.Success)
                    MessagePayUser.SendMessage(match.Groups["user"].Value,
                        Convert.ToDecimal(match.Groups["value"].Value, CultureInfo.InvariantCulture),
                        match.Groups["reason"].Value);
                else
                    MyAPIGateway.Utilities.ShowMessage("PAY", "Missing parameter - /pay user amount reason");
                return true;
            }
            #endregion pay

            #region buy
            // buy command
            if (split[0].Equals("/buy", StringComparison.InvariantCultureIgnoreCase))
            {
                //initially we should work only with a players inventory size
                //worst case scenario overflow gets dropped at the players feet which if they
                //are standing on a collector should be adequte for Alpha milestone compliance.
                //Alternately we setup an "item bank" which works like the bank account, but allows 
                //materials to be stored too.. but that makes raids somewhat pointless.
                //buy command should be pretty basic since we are buying from appropriate market at requested price.
                //if in range of valid trade region
                //examples: /buy 100 uranium (buy 100 uranium starting at lowest offer price if you can afford it, give error if you cant)
                // /buy 100 uranium 0.5 (buy up to 100 uranium if the price is below 0.5, post a buy offer for any remaining amount if price goes over)
                // /buy 100 uranium 0.5 bob (ask bob to sell you 100 uranium at 0.5, or if bob already has a sell offer at or below 0.5, buy only from him)
                // /buy cancel ??? (possible command to close an open buy offer?)
                //
                //  if (no price specified) buy whatever the lowest offer is, accumulating qty, and adding up each price point qty until
                //  sale is concluded at desired qty, or player runs out of money, or there are none left to buy.
                //  if (they place an offer above/below market && have access to offers), post offer, and deduct funds
                //  if they choose to cancel an offer, remove offer and return remaining money

                match = Regex.Match(messageText, BuyPattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    decimal buyPrice;
                    bool useMarketSellPrice = false;
                    bool buyFromMerchant = false;
                    bool findOnMarket = false;

                    string itemName = match.Groups["item"].Value;
                    string sellerName = match.Groups["user"].Value;
                    decimal buyQuantity = Convert.ToDecimal(match.Groups["qty"].Value, CultureInfo.InvariantCulture);
                    if (!decimal.TryParse(match.Groups["price"].Value, out buyPrice))
                        // We will use the they price they set at which they will buy from the player.
                        useMarketSellPrice = true; // buyprice will be 0 because TryParse failed.

                    if (string.IsNullOrEmpty(sellerName))
                        buyFromMerchant = true;

                    if (!useMarketSellPrice && buyFromMerchant)
                    {
                        // A price was specified, we actually intend to offer the item for sale at a set price to the market, not the bank.
                        findOnMarket = true;
                        buyFromMerchant = false;
                    }

                    MyObjectBuilder_Base content;
                    Dictionary<string, MyPhysicalItemDefinition> options;
                    // Search for the item and find one match only, either by exact name or partial name.
                    if (!Support.FindPhysicalParts(itemName, out content, out options))
                    {
                        if (options.Count == 0)
                            MyAPIGateway.Utilities.ShowMessage("BUY", "Item name not found.");
                        else if (options.Count > 10)
                            MyAPIGateway.Utilities.ShowMissionScreen("Item not found", itemName, " ", "Did you mean:\r\n" + String.Join(", ", options.Keys) + " ?", null, "OK");
                        else
                            MyAPIGateway.Utilities.ShowMessage("Item not found. Did you mean", String.Join(", ", options.Keys) + " ?");
                        return true;
                    }

                    MessageBuy.SendMessage(sellerName, buyQuantity, content.TypeId.ToString(), content.SubtypeName, buyPrice, useMarketSellPrice, buyFromMerchant, findOnMarket);
                    return true;
                }

                switch (split.Length)
                {
                    case 1: //ie /buy
                        MyAPIGateway.Utilities.ShowMessage("BUY", "/buy #1 #2 #3 #4");
                        MyAPIGateway.Utilities.ShowMessage("BUY", "#1 is quantity, #2 is item, #3 optional price to offer #4 optional where to buy from");
                        return true;
                }

                MyAPIGateway.Utilities.ShowMessage("BUY", "Nothing nearby to trade with?");
                return true;
            }
            #endregion buy

            #region set
            // set command - must be admin this may prove to be our most fiddly command may need a rewrite eventually
            // perhaps we limit set command to item managment and add another command like /option for the constant settings
            // allows an admin to maintain on hand, blacklist, buy and sell prices of npc market
            // The regex needs fine tuning but should basically work
            //Usage: /set <qty> "item name"   (to set the on hand stock of an item)
            //       /set blacklist "item name" (to toggle blacklist on or off (depending on current state))
            //       /set buy "item name"  <price>
            //       /set sell "item name" <price>
            // also will eventually set options like limited or unlimited stock, range checks size (or on or off), currency name
            // and trading on or off.   Or any other options an admin should be able to set in game.
            // we need a /check command maybe to display current blacklist status, price and stock too

            if (split[0].Equals("/set", StringComparison.InvariantCultureIgnoreCase) && MyAPIGateway.Session.Player.IsAdmin())
            {
                decimal amount = 0;
                string itemName = "";
                bool setbuy = false;
                bool setsell = false;
                bool blacklist = false;
                bool tradezone = false;
                match = Regex.Match(messageText, SetPattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    itemName = match.Groups["item"].Value.Trim();
                    setbuy = match.Groups["buy"].Value.Equals("buy", StringComparison.InvariantCultureIgnoreCase);
                    setsell = match.Groups["sell"].Value.Equals("sell", StringComparison.InvariantCultureIgnoreCase);
                    blacklist = match.Groups["tradezone"].Value.Equals("tradezone", StringComparison.InvariantCultureIgnoreCase);
                    blacklist = match.Groups["blacklist"].Value.Equals("blacklist", StringComparison.InvariantCultureIgnoreCase);
                    if (!blacklist && !setbuy && !setsell) // must be setting on hand
                        if (!decimal.TryParse(match.Groups["qty"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out amount))
                            amount = 0;

                    if (setbuy || setsell) // must be setting a price
                        if (!decimal.TryParse(match.Groups["price"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out amount))
                            amount = 0;

                    //ok what item are we setting?
                    MyObjectBuilder_Base content = null;
                    Dictionary<string, MyPhysicalItemDefinition> options;
                    // Search for the item and find one match only, either by exact name or partial name.
                    if (!Support.FindPhysicalParts(itemName, out content, out options))
                    {
                        if (options.Count == 0)
                            MyAPIGateway.Utilities.ShowMessage("SET", "Item name not found.");
                        else if (options.Count > 10)
                            MyAPIGateway.Utilities.ShowMissionScreen("Item not found", itemName, " ", "Did you mean:\r\n" + String.Join(", ", options.Keys) + " ?", null, "OK");
                        else
                            MyAPIGateway.Utilities.ShowMessage("Item not found. Did you mean", String.Join(", ", options.Keys) + " ?");
                        return true;
                    }
                    MyAPIGateway.Utilities.ShowMessage("SET", "Configuring {0} {1}", content.TypeId.ToString(), content.SubtypeName);

                    // TODO: do range checks for the market, using MarketManager.FindMarketsFromLocation()

                    // TODO: get more from the /SET command to set prices and Blacklist.
                    // aha i see what you did there SetMarketItemType.Quantity, SetMarketItemType.Prices, SetMarketItemType.Blacklisted
                    // on it soon hopefully
                    //SendMessage(ulong marketId, string itemTypeId, string itemSubTypeName, SetMarketItemType setType,
                    // decimal itemQuantity, decimal itemBuyPrice, decimal itemSellPrice, bool blackListed)
                    if (blacklist) //we want to black list
                    {
                        MessageSet.SendMessage(EconomyConsts.NpcMerchantId, content.TypeId.ToString(), content.SubtypeName, SetMarketItemType.Blacklisted, 0, 0, 0);
                    }
                    else if (setbuy) // do we want to set buy price..?
                    {
                        MessageSet.SendMessageBuy(EconomyConsts.NpcMerchantId, content.TypeId.ToString(), content.SubtypeName, amount);
                    }
                    else if (setsell) //or do we want to set sell price..?
                    {
                        MessageSet.SendMessageSell(EconomyConsts.NpcMerchantId, content.TypeId.ToString(), content.SubtypeName, amount);
                    }
                    else if (tradezone) //or do we want to manage tradezone..?
                    {
                        //first work out what our location is assign to mypos
                        
                        //if nothing was typed in after tradezone ("/set tradezone") or we used move ("/set tradezone move 1,2,3")
                            //if more than one NPC market exists and we didnt specify "move"
                                //show error sorry dont know which market instance to move
                            //else
                                //if move specified 
                                    //write mypos over the specified market location (1,2,3) (if it exists, show error otherwise)     
                                //else   
                                    //write mypos over the top of the single NPC market location
                        //else 
                            //if we just typed add ("/set tradezone add")
                                //add another NPC market entry at location mypos
                            //else we typed more than just add ("/set tradezone add 1 2 3") 
                                //add another NPC market entry at location 1 2 3
                            //if we just typed remove ("/set tradezone remove")
                                //show error must specify location to remove
                            //else we typed more than just remove ("/set tradezone remove 1 2 3") 
                                //remove NPC market entry at location 1 2 3
                            //if we just typed list ("/set tradezone list")
                                //display a list of all NPC trade zone coordinates
                            //else 
                                //ignore
                            
                          

                    }
                    else //no we must want to set on hand?
                    {
                        MessageSet.SendMessageQuantity(EconomyConsts.NpcMerchantId, content.TypeId.ToString(), content.SubtypeName, amount);
                    }
                    //whatever we did we are done now
                    return true;
                }




                MyAPIGateway.Utilities.ShowMessage("SET", "/set #1 #2 #3");
                //MyAPIGateway.Utilities.ShowMessage("SET", "#1 is quantity, #2 is item");
                MyAPIGateway.Utilities.ShowMessage("SET", "#1 is quantity or blacklist (or buy or sell), #2 is item, (#3 is price)");
                MyAPIGateway.Utilities.ShowMessage("SET", "eg /set 1 rifle, /set blacklist rifle, /set buy rifle 1000, /set sell rifle 2000");
                return true;
            }
            #endregion set

            #region sell
            // sell command
            if (split[0].Equals("/sell", StringComparison.InvariantCultureIgnoreCase))
            {


                //now we need to check if range is ignored, or perform a range check on [4] to see if it is in selling range
                //if both checks fail tell player nothing is near enough to trade with
                decimal sellQuantity = 0;
                bool sellAll;
                string itemName = "";
                decimal sellPrice = 1;
                bool useMarketBuyPrice = false;
                string buyerName = "";
                bool sellToMerchant = false;
                bool offerToMarket = false;

                match = Regex.Match(messageText, SellPattern, RegexOptions.IgnoreCase);
                //just have to figure out how to read coords and take stuff out of inventory and it should be pretty straightforward..
                //first off is there any point in proceeding


                //in this release the only valid trade area is the default 0:0:0 area of map or other players?
                //but should still go through the motions so backend is ready
                //by default in initialisation we alerady created a bank entry for the NPC
                //now we take our regex fields populated above and start checking
                //what the player seems to want us to do - switch needs to be converted to the regex populated fields
                //using split at the moment for debugging and structruing desired logic

                //ok we need to catch the target (player/faction) at [4] or set it to NPC if its null
                //then populate the other fields.  
                //string reply = "match " + match.Groups["qty"].Value + match.Groups["item"].Value + match.Groups["user"].Value + match.Groups["price"].Value;
                if (match.Success)
                {
                    itemName = match.Groups["item"].Value.Trim();
                    buyerName = match.Groups["user"].Value;
                    sellAll = match.Groups["qtyall"].Value.Equals("all", StringComparison.InvariantCultureIgnoreCase);
                    if (!sellAll)
                        sellQuantity = Convert.ToDecimal(match.Groups["qty"].Value, CultureInfo.InvariantCulture);
                    if (!decimal.TryParse(match.Groups["price"].Value, out sellPrice))
                        // We will use the they price they set at which they will buy from the player.
                        useMarketBuyPrice = true;  // sellprice will be 0 because TryParse failed.

                    if (string.IsNullOrEmpty(buyerName))
                        sellToMerchant = true;

                    if (!useMarketBuyPrice && sellToMerchant)
                    {
                        // A price was specified, we actually intend to offer the item for sale at a set price to the market, not the bank.
                        offerToMarket = true;
                        sellToMerchant = false;
                    }

                    MyObjectBuilder_Base content = null;
                    Dictionary<string, MyPhysicalItemDefinition> options;
                    // Search for the item and find one match only, either by exact name or partial name.
                    if (!Support.FindPhysicalParts(itemName, out content, out options))
                    {
                        // TODO: filter according to items in player inventory.
                        //options = options.Where(e =>  check inventory ..(e.Value.Id.TypeId.ToString(), e.Value.Id.SubtypeName)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                        if (options.Count == 0)
                            MyAPIGateway.Utilities.ShowMessage("SELL", "Item name not found.");
                        else if (options.Count > 10)
                            MyAPIGateway.Utilities.ShowMissionScreen("Item not found", itemName, " ", "Did you mean:\r\n" + String.Join(", ", options.Keys) + " ?", null, "OK");
                        else
                            MyAPIGateway.Utilities.ShowMessage("Item not found. Did you mean", String.Join(", ", options.Keys) + " ?");
                        return true;
                    }

                    if (sellAll)
                    {
                        var character = MyAPIGateway.Session.Player.GetCharacter();
                        // TODO: may have to recheck that character is not null.
                        var inventory = character.GetPlayerInventory();
                        sellQuantity = (decimal)inventory.GetItemAmount(content.GetId());

                        if (sellQuantity == 0)
                        {
                            MyAPIGateway.Utilities.ShowMessage("SELL", "You don't have any '{0}' to sell.", content.GetDisplayName());
                            return true;
                        }
                    }

                    // TODO: add items into holding as part of the sell message, from container Id: inventory.Owner.EntityId.
                    MessageSell.SendSellMessage(buyerName, sellQuantity, content.TypeId.ToString(), content.SubtypeName, sellPrice, useMarketBuyPrice, sellToMerchant, offerToMarket);
                    return true;
                }


                switch (split.Length)
                {
                    // everything below here is not used but kept for reference for later functions
                    case 2: //ie /sell all or /sell cancel or /sell accept or /sell deny
                        if (split[1].Equals("cancel", StringComparison.InvariantCultureIgnoreCase))
                        {
                            MessageSell.SendCancelMessage();
                            return true;
                        }
                        if (split[1].Equals("accept", StringComparison.InvariantCultureIgnoreCase))
                        {
                            MessageSell.SendAcceptMessage();
                            return true;
                        }
                        if (split[1].Equals("deny", StringComparison.InvariantCultureIgnoreCase))
                        {
                            MessageSell.SendDenyMessage();
                            return true;
                        }
                        if (split[1].Equals("collect", StringComparison.InvariantCultureIgnoreCase))
                        {
                            MessageSell.SendCollectMessage();
                            return true;
                        }
                        //return false;
                        break;
                        //default: //must be more than 2 and invalid

                        // Accessing EconomyScript.Instance.Config.NpcMerchantName here will cause an exception, as Config is only loaded on the Server, not Clients.
                        // Need to make sure core functionality is run on the Server.

                        /*                        if (buyerName != EconomyScript.Instance.Config.NpcMerchantName && buyerName != "OFFER")
                                                {
                                                    //must be selling to a player (or faction ?)
                                                    //check the item to sell is a valid product, do usual qty type checks etc
                                                    //check the player / faction exists etc etc
                                                    MyAPIGateway.Utilities.ShowMessage("SELL", "Debug: We are selling to a player send request prompt or if it is a faction check they are trading this item");
                                                }
                                                else
                                                {
                                                    if (buyerName == EconomyScript.Instance.Config.NpcMerchantName) { MyAPIGateway.Utilities.ShowMessage("SELL", "Debug: We must be selling to NPC - skip prompts sell immediately at price book price"); }
                                                    if (buyerName == "OFFER") { MyAPIGateway.Utilities.ShowMessage("SELL", "Debug: We must be posting a sell offer to stockmarket - skip prompts post offer UNLESS we match a buy offer at the same price then process that"); }
                                                }*/
                        //return false; 
                }

                MyAPIGateway.Utilities.ShowMessage("SELL", "/sell #1 #2 #3 #4");
                MyAPIGateway.Utilities.ShowMessage("SELL", "#1 is quantity, #2 is item, #3 optional price to offer #4 optional where to sell to");
                return true;
            }
            #endregion sell

            #region seen
            // seen command
            if (split[0].Equals("/seen", StringComparison.InvariantCultureIgnoreCase))
            {
                match = Regex.Match(messageText, SeenPattern, RegexOptions.IgnoreCase);
                if (match.Success)
                    MessagePlayerSeen.SendMessage(match.Groups["user"].Value);
                else
                    MyAPIGateway.Utilities.ShowMessage("SEEN", "Who are we looking for?");
                return true;
            }
            #endregion seen

            #region bal
            // bal command
            if (split[0].Equals("/bal", StringComparison.InvariantCultureIgnoreCase))
            {
                match = Regex.Match(messageText, BalPattern, RegexOptions.IgnoreCase);
                if (match.Success)
                    MessageBankBalance.SendMessage(match.Groups["user"].Value);
                else
                    MyAPIGateway.Utilities.ShowMessage("BAL", "Incorrect parameters");
                return true;
            }
            #endregion bal

            #region value
            // value command for looking up the table price of an item.
            // eg /value itemname optionalqty
            // !!!is it possible to make this work more like the bal or pay command so
            // that the same item search can be reused in buy and sell too!!! ?
            if (split[0].Equals("/value", StringComparison.InvariantCultureIgnoreCase))
            {
                match = Regex.Match(messageText, ValuePattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var itemName = match.Groups["Key"].Value;
                    var strAmount = match.Groups["Value"].Value;
                    MyObjectBuilder_Base content;
                    Dictionary<string, MyPhysicalItemDefinition> options;

                    // Search for the item and find one match only, either by exact name or partial name.
                    if (!Support.FindPhysicalParts(itemName, out content, out options))
                    {
                        if (options.Count == 0)
                            MyAPIGateway.Utilities.ShowMessage("VALUE", "Item name not found.");
                        else if (options.Count > 10)
                            MyAPIGateway.Utilities.ShowMissionScreen("Item not found", itemName, " ", "Did you mean:\r\n" + String.Join(", ", options.Keys) + " ?", null, "OK");
                        else
                            MyAPIGateway.Utilities.ShowMessage("Item not found. Did you mean", String.Join(", ", options.Keys) + " ?");
                        return true;
                    }
                    if (content != null)
                    {
                        decimal amount;
                        if (!decimal.TryParse(strAmount, out amount))
                            amount = 1; // if it cannot parse it, assume it is 1. It may not have been specified.

                        if (amount < 0) // if a negative value is provided, make it 1.
                            amount = 1;

                        if (content.TypeId != typeof(MyObjectBuilder_Ore) && content.TypeId != typeof(MyObjectBuilder_Ingot))
                        {
                            // must be whole numbers.
                            amount = Math.Round(amount, 0);
                        }

                        // Primary checks for the component are carried out Client side to reduce processing time on the server. not that 2ms matters but if 
                        // there is thousands of these requests at once one day in "space engineers the MMO" or on some auto-trading bot it might become a problem
                        MessageMarketItemValue.SendMessage(content.TypeId.ToString(), content.SubtypeName, amount, content.GetDisplayName());
                        return true;
                    }

                    MyAPIGateway.Utilities.ShowMessage("VALUE", "Unknown Item. Could not find the specified name.");
                }
                else
                    MyAPIGateway.Utilities.ShowMessage("VALUE", "You need to specify something to value eg /value ice");
                return true;
            }
            #endregion value

            #region pricelist

            if (split[0].Equals("/pricelist", StringComparison.InvariantCultureIgnoreCase))
            {
                bool showOre = false;
                bool showIngot = false;
                bool showComponent = false;
                bool showAmmo = false;
                bool showTools = false;

                foreach (var str in split)
                {
                    if (str.StartsWith("ore", StringComparison.InvariantCultureIgnoreCase))
                        showOre = true;
                    if (str.StartsWith("ingot", StringComparison.InvariantCultureIgnoreCase))
                        showIngot = true;
                    if (str.StartsWith("component", StringComparison.InvariantCultureIgnoreCase))
                        showComponent = true;
                    if (str.StartsWith("ammo", StringComparison.InvariantCultureIgnoreCase))
                        showAmmo = true;
                    if (str.StartsWith("tool", StringComparison.InvariantCultureIgnoreCase))
                        showTools = true;
                }
                MessageMarketPriceList.SendMessage(showOre, showIngot, showComponent, showAmmo, showTools);
            }

            #endregion

            #region worth
            // worth command
            if (split[0].Equals("/worth", StringComparison.InvariantCultureIgnoreCase))
            {
                var selectedShip = Support.FindLookAtEntity(MyAPIGateway.Session.ControlledObject, true, false, false, false, false, false, false) as IMyCubeGrid;
                if (selectedShip != null)
                {
                    MessageWorth.SendMessage(selectedShip.EntityId);
                    return true;
                }
                MyAPIGateway.Utilities.ShowMessage("WORTH", "You need to target a ship or station to value.");
                return true;
            }
            #endregion

            #region accounts
            // accounts command.  For Admins only.
            if (split[0].Equals("/accounts", StringComparison.InvariantCultureIgnoreCase) && MyAPIGateway.Session.Player.IsAdmin())
            {
                MessageListAccounts.SendMessage();
                return true;
                // don't respond to non-admins.
            }
            #endregion accounts

            #region reset
            // reset command.  For Admins only.
            if (split[0].Equals("/reset", StringComparison.InvariantCultureIgnoreCase) && MyAPIGateway.Session.Player.IsAdmin())
            {
                MessageResetAccount.SendMessage();
                return true;
                // don't respond to non-admins.
            }
            #endregion reset

            #region ver
            //ver reply
            if (split[0].Equals("/ver", StringComparison.InvariantCultureIgnoreCase))
            {
                string versionreply = EconomyConsts.MajorVer + " " + EconomyConsts.ModCommunicationVersion;
                MyAPIGateway.Utilities.ShowMessage("VER", versionreply);
                return true;
            }
            #endregion ver

            #region news system
            //news reply - early proof of concept work
            //this should fire a sub with the parameter as the message to be displayed, that way if we get other things like bounties or
            //navigation hazard warnings and random frontier news working, we can just throw the generated news report at 
            //the global news sub to make it display
            //clearly this needs to run server side and trigger on all online players screens - 
            //this test will only display on the current admins screen however
            //MyAPIGateway.Utilities.ShowMissionScreen("Economy", "", "Warning", "This is only a placeholder mod it is not functional yet!", null, "Close");
            if (split[0].Equals("/global", StringComparison.InvariantCultureIgnoreCase) && MyAPIGateway.Session.Player.IsAdmin())
            {

                string announce = "Date Now " + messageText;
                //messageTexts needs the "/global" removed, and add a missionbox newline on each \n  then display the formatted
                //message in a mission box. Probably need a way to add new lines from chat eg line1^Line2^Line3
                MyAPIGateway.Utilities.ShowMissionScreen("Economy", "", "News:", announce, null, "Close");
                return true;
            }

            //this is the normal players command to bring up the news log (newest to oldest) and/or last major news message
            if (split[0].Equals("/news", StringComparison.InvariantCultureIgnoreCase))
            {

                string announce = "No news is good news?";
                //messageTexts Displays the last 10 or so /global messages with time stamp or recent events (such as attacks, new markets being registered etc)
                MyAPIGateway.Utilities.ShowMissionScreen("Economy", "", "News:", announce, null, "Close");
                return true;
            }
            #endregion news system

            #region help
            // help command
            if (split[0].Equals("/ehelp", StringComparison.InvariantCultureIgnoreCase))
            {
                if (split.Length <= 1)
                {
                    //did we just type ehelp? show what else they can get help on
                    //might be better to make a more detailed help reply here using mission window later on
                    MyAPIGateway.Utilities.ShowMessage("ehelp", "Commands: ehelp, bal, pay, seen, buy, sell, value, ver, lcd, news, worth");
                    if (MyAPIGateway.Session.Player.IsAdmin())
                    {
                        MyAPIGateway.Utilities.ShowMessage("Admin ehelp", "Commands: accounts, bal player, reset, set, global, pay player +/-any_amount");
                    }
                    MyAPIGateway.Utilities.ShowMessage("ehelp", "Try '/ehelp command' for more informations about specific command");
                    return true;
                }
                else
                {
                    string helpreply = "."; //this reply is too big need to move it to pop up \r\n
                    switch (split[1].ToLowerInvariant())
                    {
                        // did we type /ehelp help ?
                        case "help":
                            MyAPIGateway.Utilities.ShowMessage("/ehelp #", "Displays help on the specified command [#].");
                            return true;
                        // did we type /help buy etc
                        case "pay":
                            MyAPIGateway.Utilities.ShowMessage("eHelp", "/pay X Y Z Pays player [x] amount [Y] [for reason Z]");
                            MyAPIGateway.Utilities.ShowMessage("eHelp", "Example: /pay bob 100 being awesome");
                            MyAPIGateway.Utilities.ShowMessage("eHelp", "for larger player names used quotes eg \"bob the builder\"");
                            if (MyAPIGateway.Session.Player.IsAdmin())
                            {
                                MyAPIGateway.Utilities.ShowMessage("Admin eHelp", "Admins can add or remove any amount from a player, including negative");
                            }
                            return true;
                        case "worth":
                            MyAPIGateway.Utilities.ShowMessage("eHelp", "/worth  Tells you the value of the target object in relation to how much the components used to create it are worth.");
                            MyAPIGateway.Utilities.ShowMessage("eHelp", "Useful if you want to sell a ship or station and need a price. Example: /worth");
                            return true;
                        case "seen":
                            MyAPIGateway.Utilities.ShowMessage("eHelp", "/seen X Displays time and date that economy plugin last saw player X");
                            MyAPIGateway.Utilities.ShowMessage("eHelp", "Example: /seen bob");
                            return true;
                        case "global":
                            if (MyAPIGateway.Session.Player.IsAdmin()) { MyAPIGateway.Utilities.ShowMessage("Admin eHelp", "/global displays a mission window message to all online players"); return true; }
                            else { return false; }
                        case "accounts":
                            if (MyAPIGateway.Session.Player.IsAdmin()) { MyAPIGateway.Utilities.ShowMessage("Admin eHelp", "/accounts displays all player balances"); return true; }
                            else { return false; }
                        case "reset":
                            if (MyAPIGateway.Session.Player.IsAdmin()) { MyAPIGateway.Utilities.ShowMessage("Admin eHelp", "/reset resets your balance to 100"); return true; }
                            else { return false; }
                        case "set":
                            if (MyAPIGateway.Session.Player.IsAdmin()) { MyAPIGateway.Utilities.ShowMessage("Admin eHelp", "/set # X  Sets the on hand amount of item (X) to amount  (#) in the NPC market. Eventually will set other settings too."); return true; }
                            else { return false; }
                        case "bal":
                            MyAPIGateway.Utilities.ShowMessage("eHelp", "/bal Displays your bank balance");
                            MyAPIGateway.Utilities.ShowMessage("eHelp", "Example: /bal");
                            if (MyAPIGateway.Session.Player.IsAdmin())
                            {
                                MyAPIGateway.Utilities.ShowMessage("Admin eHelp", "Admins can also view another player. eg. /bal bob");
                            }
                            return true;
                        case "buy":
                            MyAPIGateway.Utilities.ShowMessage("eHelp", "/buy W X Y Z - Purchases a quantity [W] of item [X] [at price Y] [from player Z]");
                            MyAPIGateway.Utilities.ShowMessage("eHelp", "Example: /buy 20 Ice ");
                            return true;
                        case "sell":
                            helpreply = "/sell W X Y Z \r\n Sells quantity [W] of item [X] [at price Y] [to player Z]\r\n" +
                                " Eg sell to another player /sell 1 rifle 10 Bob\r\n" +
                                " or Sell to NPC (or nearest market) Eg: /sell 20 Ice \r\n" +
                                "/sell cancel (Cancels any sell offers you did)\r\n" +
                                "/sell collect (collects items from failed sell offers)\r\n" +
                                "/sell acccept|deny (accepts or rejects a sell offer made to you)\r\n";
                            MyAPIGateway.Utilities.ShowMessage("eHelp", "Example: /sell 20 Ice [optional price] [optional player]");
                            MyAPIGateway.Utilities.ShowMissionScreen("Economy Help", "", "sell command", helpreply, null, "Close");
                            return true;
                        case "value":
                            MyAPIGateway.Utilities.ShowMessage("eHelp", "/value X Y - Looks up item [X] of optional quantity [Y] and reports the buy and sell value.");
                            MyAPIGateway.Utilities.ShowMessage("eHelp", "Example: /value Ice 20    or   /value ice");
                            return true;
                        case "news":
                            MyAPIGateway.Utilities.ShowMessage("eHelp", "/news Displays a news log of the last few signifiant server events");
                            MyAPIGateway.Utilities.ShowMessage("eHelp", "Eg, Pirate attacks, bounties, server competitions etc Example: /news");
                            return true;
                        case "ver":
                            MyAPIGateway.Utilities.ShowMessage("eHelp", "/ver Displays the diagnostic version of running Economy script");
                            return true;

                    }
                }
            }
            #endregion help

            // it didnt start with help or anything else that matters so return false and get us out of here;
            return false;
        }
        #endregion command list

        #region SetLanguage

        /// <summary>
        /// Sets the CultureInfo to use when formatting numbers and dates on the server, and the text resources when fetching names of game objects to display or send back to players.
        /// </summary>
        private void SetLanguage()
        {
            MyTexts.LanguageDescription language = MyTexts.Languages.ContainsKey(Config.Language) ? MyTexts.Languages[Config.Language] : MyTexts.Languages[0];
            ServerCulture = CultureInfo.GetCultureInfo(language.FullCultureName);

            // Load resources for that language.
            // This may acutally interfere with other mods or the server itself that are dependant the Text resources of the game.
            MyTexts.Clear();
            MyTexts.LoadTexts(Path.Combine(MyAPIGateway.Utilities.GamePaths.ContentPath, "Data", "Localization"), language.CultureName, language.SubcultureName);
        } 

        #endregion
    }
}