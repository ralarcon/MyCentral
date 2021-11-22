using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Processor;
using Azure.Storage.Blobs;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace MyCentral.Client.Azure
{
    public class AzureEventClient : IEventClient
    {
        private readonly BlobContainerClient storageClient;
        private readonly EventProcessorClient processor;
        private ConcurrentDictionary<string, int> partitionEventCount = new ConcurrentDictionary<string, int>();

        public List<IObserver<Event>> observers { get; set; } = null!;

        public DateTime lastEventReceived = DateTime.MinValue;

        private readonly TimeSpan MaxWaitTime = TimeSpan.FromSeconds(15);

        public AzureEventClient(string eventConnectionString, string blobStorageConnectionString, string blobContainerName)
        {
            string consumerGroup = EventHubConsumerClient.DefaultConsumerGroupName;
            storageClient = new BlobContainerClient(blobStorageConnectionString, blobContainerName);

            EventProcessorClientOptions clientOptions = new EventProcessorClientOptions()
            {
                MaximumWaitTime = MaxWaitTime,
                PrefetchCount = 100,
                CacheEventCount = 100,
                Identifier = "Broadcast-raw-messages"
            };

            processor = new EventProcessorClient(storageClient, consumerGroup, eventConnectionString, clientOptions);
            processor.PartitionInitializingAsync += InitializeEventHandler;
            processor.ProcessEventAsync += ProcessorEventHandler;
            processor.ProcessErrorAsync += ProcessorErrorHandler;
            processor.StartProcessing();

            observers = new List<IObserver<Event>>();
        }

        private class Unsubscriber : IDisposable
        {
            private List<IObserver<Event>> _observers;
            private IObserver<Event> _observer;

            public Unsubscriber(List<IObserver<Event>> observers, IObserver<Event> observer)
            {
                this._observers = observers;
                this._observer = observer;
            }

            public void Dispose()
            {
                if (!(_observer == null)) _observers.Remove(_observer);
            }
        }

        public IDisposable Subscribe(IObserver<Event> observer)
        {
            if (!observers.Contains(observer))
                observers.Add(observer);

            return new Unsubscriber(observers, observer);
        }
        public async ValueTask DisposeAsync()
        {
            await processor.StopProcessingAsync();

            processor.PartitionInitializingAsync -= InitializeEventHandler;
            processor.ProcessEventAsync -= ProcessorEventHandler;
            processor.ProcessErrorAsync -= ProcessorErrorHandler;

            foreach (var observer in observers.ToArray())
                if (observer != null) observer.OnCompleted();

            observers.Clear();
        }


        private Task InitializeEventHandler(PartitionInitializingEventArgs args)
        {
            try
            {
                if (args.CancellationToken.IsCancellationRequested)
                {
                    return Task.CompletedTask;
                }

                // If no checkpoint was found, start processing
                // events enqueued now or in the future.

                EventPosition startPositionWhenNoCheckpoint =
                    EventPosition.FromEnqueuedTime(DateTimeOffset.UtcNow);

                args.DefaultStartingPosition = startPositionWhenNoCheckpoint;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Unexpected error in ProcessorErrorHandler. " + ex.ToString());
                throw;
            }

            return Task.CompletedTask;
        }

        private async Task ProcessorEventHandler(ProcessEventArgs arg)
        {
            try
            {
                if (arg.CancellationToken.IsCancellationRequested)
                {
                    //Cancel immediately
                    return;
                }

                string partition = arg.Partition.PartitionId;
                Event eventToBroadcast = null;

                if (arg.HasEvent)
                {
                    lastEventReceived = DateTime.UtcNow;
                    eventToBroadcast = new Event(GetDeviceId(arg.Data.SystemProperties), GetSubject(arg.Data.SystemProperties), arg.Data.EnqueuedTime, arg.Data.EventBody.ToString());

                    int eventsSinceLastCheckpoint = partitionEventCount.AddOrUpdate(
                        key: partition,
                        addValue: 1,
                        updateValueFactory: (_, currentCount) => currentCount + 1);

                    if (eventsSinceLastCheckpoint >= 50)
                    {
                        //Update the checkpoint every 50 delivered messages
                        await arg.UpdateCheckpointAsync();
                        partitionEventCount[partition] = 0;
                    }
                }
                else
                {
                    if (DateTime.UtcNow - lastEventReceived > MaxWaitTime)
                    {
                        eventToBroadcast = new Event(String.Empty, "Processor Heartbeat", DateTime.UtcNow, "Event processor is working...");
                    }
                }

                if (eventToBroadcast != null)
                {
                    foreach (var observer in observers)
                    {
                        observer.OnNext(eventToBroadcast);
                    }
                }

            }
            catch(Exception ex)
            {
                foreach (var observer in observers)
                {
                    observer.OnNext(new Event(string.Empty, "Error handling event", DateTime.UtcNow, ex.ToString()));
                }
            }
        }



        private Task ProcessorErrorHandler(ProcessErrorEventArgs arg)
        {
            try
            {
                foreach (var observer in observers)
                {
                    observer.OnError(new Exception($"Error processing events. Operation={arg.Operation}", arg.Exception));
                }
            }
            catch(Exception ex)
            {
                Debug.WriteLine("Unexpected error in ProcessorErrorHandler. " + ex.ToString());
                throw;
            }

            return Task.CompletedTask;
        }


        private static string GetSubject(IReadOnlyDictionary<string, object> properties)
        {
            if (properties.TryGetValue("dt-subject", out var subject))
            {
                return subject?.ToString() ?? string.Empty;
            }

            return string.Empty;
        }
        private static string GetDeviceId(IReadOnlyDictionary<string, object> properties)
        {
            if (properties.TryGetValue("iothub-connection-device-id", out var deviceId))
            {
                return deviceId?.ToString() ?? string.Empty;
            }

            return string.Empty;
        }
    }
}
