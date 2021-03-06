using System;
using Hocon;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cloudstate;
using CloudState.CSharpSupport.Contexts;
using CloudState.CSharpSupport.Crdt.Interfaces;
using CloudState.CSharpSupport.Crdt.Services;
using CloudState.CSharpSupport.EventSourced.Interfaces;
using CloudState.CSharpSupport.EventSourced.Services;
using CloudState.CSharpSupport.Interfaces;
using CloudState.CSharpSupport.Reflection;
using CloudState.CSharpSupport.Services;
using Grpc.Core;
using Grpc.Reflection;
using Grpc.Reflection.V1Alpha;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudState.CSharpSupport
{
    // ReSharper disable once ClassNeverInstantiated.Global
    internal class CloudStateWorker : IHostedService
    {
        public class CloudStateConfiguration
        {
            private IConfiguration Configuration { get; }

            public string Host { get; }
            public int Port { get; }
            public CloudStateConfiguration(IConfiguration configuration)
            {
                var hocon = ConfigurationFactory.Load();
                var address = hocon.GetString("cloudstate.user-host");
                var port = hocon.GetInt("cloudstate.user-port");
                
                Configuration = configuration;
                Host = (address != null ? address : "127.0.0.1");
                Port = ( port > 0 ? port : 8080);
            }
        }

        private ILoggerFactory LoggerFactory { get; }
        private IReadOnlyDictionary<string, IStatefulService> StatefulServices { get; }
        private CloudStateConfiguration Config { get; }
        private ILogger<CloudStateWorker> Logger { get; }
        private Server Server { get; }

        public CloudStateWorker(
                ILoggerFactory loggerFactory,
                IConfiguration configuration,
                IDictionary<string, IStatefulService> statefulServices
            )
        {
            LoggerFactory = loggerFactory;
            StatefulServices = new ReadOnlyDictionary<string, IStatefulService>(statefulServices);

            Config = new CloudStateConfiguration(configuration);
            Logger = LoggerFactory.CreateLogger<CloudStateWorker>();
            Server = new Server
            {
                Ports = { new ServerPort(Config.Host, Config.Port, ServerCredentials.Insecure) }
            };
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await Task.Factory.StartNew(() =>
            {
                foreach (var serviceGroup in StatefulServices.GroupBy(x => x.Value))
                {
                    switch (serviceGroup.Key)
                    {
                        case EventSourcedStatefulService _:
                            Server.Services.Add(
                                Cloudstate.Eventsourced.EventSourced.BindService(new EntityCollectionService(
                                    LoggerFactory,
                                    Config,
                                    serviceGroup.ToDictionary(
                                        x => x.Key,
                                        x => x.Value as IEventSourcedStatefulService
                                    ),
                                    new Context(new ResolvedServiceCallFactory(StatefulServices))
                                ))
                            );
                            break;
                        case CrdtStatefulService _:
                            Server.Services.Add(
                                Cloudstate.Crdt.Crdt.BindService(new CrdtEntityCollectionService(
                                    LoggerFactory,
                                    serviceGroup.ToDictionary(
                                        x => x.Key,
                                        x => x.Value as ICrdtStatefulService
                                    ),
                                    new Context(new ResolvedServiceCallFactory(StatefulServices))
                                ))
                            );
                            break;
                        default:
                            throw new NotImplementedException($"Unknown stateful service implementation of {serviceGroup.Key}");
                    }
                }

                Server.Services.Add(
                    EntityDiscovery.BindService(
                        new EntityDiscoveryService(
                            LoggerFactory,
                            StatefulServices
                        )
                    )
                );

                // TODO: Feature flag this.
                var reflectionServiceImpl = new ReflectionServiceImpl(
                    StatefulServices.Values.Select(x => x.ServiceDescriptor)
                );
                Server.Services.Add(
                    ServerReflection.BindService(
                        reflectionServiceImpl
                    )
                );

                Server.Start();
                Logger.LogInformation(
                    $"Server listening on [{Config.Host}:{Config.Port}]"
                );

            }, cancellationToken);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await Server.ShutdownAsync();
        }
    }
}