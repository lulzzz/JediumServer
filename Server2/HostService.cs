﻿using System;
using System.Configuration;
using System.IO;
using System.Security.Cryptography;
using Akka.Actor;
using Akka.Configuration;
using Akka.Configuration.Hocon;
using Akka.Interfaced;
using Common.Logging;
using Domain;
using DomainInternal;
using IniParser;
using IniParser.Model;
using Server2.Behaviours;
using Server2.Connection;
using Server2.database;
using Server2.Plugins;
using Server2.Web;
using TopShelf.Owin;

namespace Server2
{
    //Основной сервер
    public class HostService : IOwinService
    {
        private IDatabaseAgent _databaseAgent;

        private ILog _logger;
        private IObjectsManager _manager;

        private IPluginsHost _pluginsHost;
        private IConnection _serverConnection;
        private ActorSystem _system;

        private ITerminalConnection _terminal;


        private IWebApiHost _webApiHost;

        public bool Start()
        {
            _logger = LogManager.GetLogger("[MainHost]");

            _logger.Info("------------------------------JEDIUM SERVER---------------------------");
            _logger.Info("Starting at: " + DateTime.Now);

            //settings
            var parser = new FileIniDataParser();
            IniData data = parser.ReadFile("ServerConfig.ini");
            MainSettings.BehavioursPluginPath = data["Server"]["BehavioursPluginPath"];
            MainSettings.WebApiHost = data["Server"]["WebApiHost"];
            MainSettings.DBUrl = data["Server"]["DBUrl"];
            MainSettings.DatabaseName = data["Server"]["DatabaseName"];
            MainSettings.CollectMessageStats = bool.Parse(data["Statistics"]["CollectMessageStats"]);
            MainSettings.StatsCollectionInterval = int.Parse(data["Statistics"]["StatsCollectionInterval"]);


            MD5 mcalc = MD5.Create();
            byte[] dbytes = File.ReadAllBytes("Domain.dll");
            MainSettings.ServerHash = mcalc.ComputeHash(dbytes).ToHex(false);
            _logger.Info($"Server domain hash: {MainSettings.ServerHash}");

            //load plugins
            BehaviourManager.LoadBehaviours(MainSettings.BehavioursPluginPath);


           
            //get config (app.config)
            AkkaConfigurationSection section = (AkkaConfigurationSection) ConfigurationManager.GetSection("akka");
            Config aconfig = section.AkkaConfig;


            //попытаться запустить актер сервера
            try
            {
                _system = ActorSystem.Create("VirtualFramework", aconfig);
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message);
                throw;
            }

            // на случай разрыва соединения
            DeadRequestProcessingActor.Install(_system);


            _databaseAgent = _system.ActorOf(Props.Create(() => new MongoDbActor()), "DataBase")
                .Cast<DatabaseAgentRef>(); //TODO: Add test for connection

            _terminal = _system.ActorOf(Props.Create(() => new TerminalConnection(_databaseAgent)), "Terminal")
                .Cast<TerminalConnectionRef>();


            _serverConnection = _system
                .ActorOf(Props.Create(() => new ServerConnection(_databaseAgent)), "ServerEndpoint")
                .Cast<ConnectionRef>(); //TODO: find me

            //_databaseAgent.SetDummyObjectTest().Wait();


            _manager = _system
                .ActorOf(Props.Create(() => new ObjectsManager(_databaseAgent, _serverConnection)), "ObjectManager")
                .Cast<ObjectsManagerRef>();

            //assets host
            _webApiHost = _system
                .ActorOf(Props.Create(() => new WebApiHost(MainSettings.WebApiHost, _databaseAgent, _manager)),
                    "AssetsHost")
                .Cast<WebApiHostRef>();


            _pluginsHost = _system.ActorOf(Props.Create(() => new PluginsHost(_databaseAgent, _manager)), "PluginsHost")
                .Cast<PluginsHostRef>();

            _pluginsHost.LoadPlugins().Wait();


            _manager.LoadAllScenes().Wait();


            return true;
        }


        public bool Stop()
        {
            // 

            _logger.Info("Saving to DB");

            _manager.SaveObjectsToDB().Wait();

            _logger.Info("Shutting down at: " + DateTime.Now);


            _system.Terminate().Wait();
            return true;
        }
    }

    public static class MainSettings
    {
        public static string BehavioursPluginPath;
        public static string WebApiHost;
        public static string DBUrl;
        public static string DatabaseName;

        //stats
        public static bool CollectMessageStats;
        public static int StatsCollectionInterval;

        //connection
        public static string ServerHash;
    }
}