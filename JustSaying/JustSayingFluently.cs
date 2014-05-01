using System;
using Amazon;
using JustSaying.AwsTools;
using JustSaying.AwsTools.QueueCreation;
using JustSaying.Messaging;
using JustSaying.Messaging.MessageHandling;
using JustSaying.Messaging.MessageProcessingStrategies;
using JustSaying.Messaging.Messages;
using JustSaying.Messaging.MessageSerialisation;
using JustSaying.Messaging.Monitoring;
using NLog;
using JustSaying.Lookups;

namespace JustSaying
{
    /// <summary>
    /// Fluently configure a JustSaying message bus.
    /// Intended usage:
    /// 1. Factory.JustSaying(); // Gimme a bus
    /// 2. WithMonitoring(instance) // Ensure you monitor the messaging status
    /// 3. Set subscribers - WithSqsTopicSubscriber() / WithSnsTopicSubscriber() etc // ToDo: Shouldn't be enforced in base! Is a JE concern.
    /// 3. Set Handlers - WithTopicMessageHandler()
    /// </summary>
    public class JustSayingFluently : IFluentSubscription, ISqsSubscriber, ISubscriberIntoQueue, ISubscriberConfigurator
    {
        private static readonly Logger Log = LogManager.GetLogger("JustSaying"); // ToDo: Dangerous!
        private readonly IVerifyAmazonQueues _amazonQueueCreator;
        protected readonly IAmJustSaying Bus;
        private string _currnetTopic;
        private SqsConfiguration _subscriptionConfig;

        internal protected JustSayingFluently(IAmJustSaying bus, IVerifyAmazonQueues queueCreator)
        {
            Bus = bus;
            _amazonQueueCreator = queueCreator;
        }

        /// <summary>
        /// Subscribe to a topic using SQS.
        /// </summary>
        /// <param name="topic">Topic to listen in on</param>
        /// <param name="messageRetentionSeconds">Time messages should be kept in this queue</param>
        /// <param name="visibilityTimeoutSeconds">Seconds message should be invisible to other other receiving components</param>
        /// <param name="instancePosition">Optional instance position as tagged by paas tools in AWS. Using this will cause the message to get handled by EACH instance in your cluster</param>
        /// <param name="onError">Optional error handler. Use this param to inject custom error handling from within the consuming application</param>
        /// <param name="maxAllowedMessagesInFlight">Configures the stack to use the Throttled handling strategy, configured to this level of concurrent messages in flight</param>
        /// <param name="messageProcessingStrategy">Hook to supply your own IMessageProcessingStrategy</param>
        /// <returns></returns>
        public IFluentSubscription WithSqsTopicSubscriber(string topic, int messageRetentionSeconds, int visibilityTimeoutSeconds = 30, int? instancePosition = null, Action<Exception> onError = null, int? maxAllowedMessagesInFlight = null, IMessageProcessingStrategy messageProcessingStrategy = null)
        {
            return WithSqsTopicSubscriber(cf =>
            {
                cf.Topic = topic;
                cf.MessageRetentionSeconds = messageRetentionSeconds;
                cf.VisibilityTimeoutSeconds = visibilityTimeoutSeconds;
                cf.InstancePosition = instancePosition;
                cf.OnError = onError;
                cf.MaxAllowedMessagesInFlight = maxAllowedMessagesInFlight;
                cf.MessageProcessingStrategy = messageProcessingStrategy;
            });
        }

        // ToDo: Move these into the factory class?
        public virtual IPublishSubscribtionEndpointProvider CreateSubscriptiuonEndpointProvider(SqsConfiguration subscriptionConfig)
        {
            return new SqsSubscribtionEndpointProvider(subscriptionConfig);
        }
        public virtual IPublishEndpointProvider CreatePublisherEndpointProvider(SqsConfiguration subscriptionConfig)
        {
            return new SnsPublishEndpointProvider(subscriptionConfig.Topic);
        }
        public IFluentSubscription WithSqsTopicSubscriber(Action<SqsConfiguration> confBuilder)
        {
            var subscriptionConfig = new SqsConfiguration();
            confBuilder(subscriptionConfig);

            var subscriptionEndpointProvider = CreateSubscriptiuonEndpointProvider(subscriptionConfig);
            var publishEndpointProvider = CreatePublisherEndpointProvider(subscriptionConfig);

            subscriptionConfig.QueueName = subscriptionEndpointProvider.GetLocationName();
            subscriptionConfig.PublishEndpoint = publishEndpointProvider.GetLocationName();
            subscriptionConfig.Validate();

            var queue = _amazonQueueCreator.VerifyOrCreateQueue(Bus.Config.Region, Bus.SerialisationRegister, subscriptionConfig);

            var sqsSubscriptionListener = new SqsNotificationListener(queue, Bus.SerialisationRegister, new NullMessageFootprintStore(), Bus.Monitor, subscriptionConfig.OnError);
            Bus.AddNotificationTopicSubscriber(subscriptionConfig.Topic, sqsSubscriptionListener);
            
            if (subscriptionConfig.MaxAllowedMessagesInFlight.HasValue)
                sqsSubscriptionListener.WithMaximumConcurrentLimitOnMessagesInFlightOf(subscriptionConfig.MaxAllowedMessagesInFlight.Value);

            if (subscriptionConfig.MessageProcessingStrategy != null)
                sqsSubscriptionListener.WithMessageProcessingStrategy(subscriptionConfig.MessageProcessingStrategy);

            Log.Info(string.Format("Created SQS topic subscription - Topic: {0}, QueueName: {1}", subscriptionConfig.Topic, subscriptionConfig.QueueName));
            _currnetTopic = subscriptionConfig.Topic;

            return this;
        }

        public IFluentSubscription WithSqsTopicSubscriber(string topic, int messageRetentionSeconds, IMessageProcessingStrategy messageProcessingStrategy)
        {
            return WithSqsTopicSubscriber(topic, messageRetentionSeconds, 30, null, null, null,
                messageProcessingStrategy);
        }

        /// <summary>
        /// Register for publishing messages to SNS
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="topic"></param>
        /// <returns></returns>
        public IAmJustSayingFluently WithSnsMessagePublisher<T>(string topic) where T : Message
        {
            Log.Info("Added publisher");

            var endpointProvider = new SnsPublishEndpointProvider(topic);
            var eventPublisher = new SnsTopicByName(
                endpointProvider.GetLocationName(),
                AWSClientFactory.CreateAmazonSimpleNotificationServiceClient(RegionEndpoint.GetBySystemName(Bus.Config.Region)),
                Bus.SerialisationRegister);

            if (!eventPublisher.Exists())
                eventPublisher.Create();

            Bus.SerialisationRegister.AddSerialiser<T>(new ServiceStackSerialiser<T>());
            Bus.AddMessagePublisher<T>(topic, eventPublisher);

            Log.Info(string.Format("Created SNS topic publisher - Topic: {0}", topic));

            return this;
        }

        /// <summary>
        /// I'm done setting up. Fire up listening on this baby...
        /// </summary>
        public void StartListening()
        {
            Bus.Start();
            Log.Info("Started listening for messages");
        }

        /// <summary>
        /// Gor graceful shutdown of all listening threads
        /// </summary>
        public void StopListening()
        {
            Bus.Stop();
            Log.Info("Stopped listening for messages");
        }

        /// <summary>
        /// Publish a message to the stack.
        /// </summary>
        /// <param name="message"></param>
        public virtual void Publish(Message message)
        {
            if (Bus == null)
                throw new InvalidOperationException("You must register for message publication before publishing a message");
            
            Bus.Publish(message);
        }

        /// <summary>
        /// States whether the stack is listening for messages (subscriptions are running)
        /// </summary>
        public bool Listening { get { return (Bus != null) && Bus.Listening; } }

        public IFluentSubscription ConfigureSubscriptionWith(Action<SqsConfiguration> configBuilder)
        {
            configBuilder(_subscriptionConfig);
            var publishEndpointProvider = CreatePublisherEndpointProvider(_subscriptionConfig);

            _subscriptionConfig.PublishEndpoint = publishEndpointProvider.GetLocationName();
            _subscriptionConfig.Validate();

            var queue = _amazonQueueCreator.VerifyOrCreateQueue(Bus.Config.Region, Bus.SerialisationRegister, _subscriptionConfig);

            var sqsSubscriptionListener = new SqsNotificationListener(queue, Bus.SerialisationRegister, new NullMessageFootprintStore(), Bus.Monitor, _subscriptionConfig.OnError);
            Bus.AddNotificationTopicSubscriber(_subscriptionConfig.Topic, sqsSubscriptionListener);

            if (_subscriptionConfig.MaxAllowedMessagesInFlight.HasValue)
                sqsSubscriptionListener.WithMaximumConcurrentLimitOnMessagesInFlightOf(_subscriptionConfig.MaxAllowedMessagesInFlight.Value);

            if (_subscriptionConfig.MessageProcessingStrategy != null)
                sqsSubscriptionListener.WithMessageProcessingStrategy(_subscriptionConfig.MessageProcessingStrategy);

            Log.Info(string.Format("Created SQS topic subscription - Topic: {0}, QueueName: {1}", _subscriptionConfig.Topic, _subscriptionConfig.QueueName));
            _currnetTopic = _subscriptionConfig.Topic;

            return this;
        }

        public ISubscriberConfigurator IntoQueue(string queuename)
        {
            _subscriptionConfig.QueueName = queuename;
            return this;
        }

        public ISubscriberIntoQueue WithSqsTopicSubscriber(string topic)
        {
            _subscriptionConfig = new SqsConfiguration() {Topic = topic};
            return this;
        }

        #region Implementation of IFluentSubscription

        /// <summary>
        /// Set message handlers for the given topic
        /// </summary>
        /// <typeparam name="T">Message type to be handled</typeparam>
        /// <param name="handler">Handler for the message type</param>
        /// <returns></returns>
        public IFluentSubscription WithMessageHandler<T>(IHandler<T> handler) where T : Message
        {
            Bus.SerialisationRegister.AddSerialiser<T>(new ServiceStackSerialiser<T>());
            Bus.AddMessageHandler(_currnetTopic, handler);

            Log.Info(string.Format("Added a message handler - Topic: {0}, MessageType: {1}, HandlerName: {2}", _currnetTopic, typeof(T).Name, handler.GetType().Name));

            return this;
        }

        #endregion

        #region Implementation of IFluentMonitoring

        /// <summary>
        /// Provide your own monitoring implementation
        /// </summary>
        /// <param name="messageMonitor">Monitoring class to be used</param>
        /// <returns></returns>
        public IAmJustSayingFluently WithMonitoring(IMessageMonitor messageMonitor)
        {
            Bus.Monitor = messageMonitor;
            return this;
        }

        #endregion

        public IAmJustSayingFluently ConfigurePublisherWith(Action<IPublishConfiguration> confBuilder)
        {
            return this;
        }
    }

    public interface IAmJustSayingFluently : IMessagePublisher, IFluentMonitoring
    {
        IAmJustSayingFluently WithSnsMessagePublisher<T>(string topic) where T : Message;
        ISubscriberIntoQueue WithSqsTopicSubscriber(string topic);
        void StartListening();
        void StopListening();
        bool Listening { get; }
    }

    public interface IFluentMonitoring
    {
        IAmJustSayingFluently WithMonitoring(IMessageMonitor messageMonitor);
    }

    public interface IFluentSubscription : IAmJustSayingFluently
    {
        IFluentSubscription WithMessageHandler<T>(IHandler<T> handler) where T : Message;
    }

    public interface ISqsSubscriber : IAmJustSayingFluently
    {
    }
    public interface ISubscriberIntoQueue : IAmJustSayingFluently
    {
        ISubscriberConfigurator IntoQueue(string queuename);
    }
    public interface ISubscriberConfigurator : IAmJustSayingFluently
    {
        IFluentSubscription ConfigureSubscriptionWith(Action<SqsConfiguration> config);
    }
}