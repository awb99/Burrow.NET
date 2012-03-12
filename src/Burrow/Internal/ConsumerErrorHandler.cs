﻿using System;
using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Burrow.Internal
{
    public class ConsumerErrorHandler : IConsumerErrorHandler
    {
        public const string ErrorQueue = "Burrow.Queue.Error";
        public const string ErrorExchange = "Burrow.Exchange.Error";

        private readonly ConnectionFactory _connectionFactory;
        private readonly ISerializer _serializer;
        private readonly IRabbitWatcher _watcher;
        private readonly object _channelGate = new object();

        private IConnection _connection;
        private bool _errorQueueDeclared;
        private bool _errorQueueBound;

        public ConsumerErrorHandler(ConnectionFactory connectionFactory, ISerializer serializer, IRabbitWatcher watcher)
        {
            if (connectionFactory == null)
            {
                throw new ArgumentNullException("connectionFactory");
            }
            if (serializer == null)
            {
                throw new ArgumentNullException("serializer");
            }
            if (watcher == null)
            {
                throw new ArgumentNullException("watcher");
            }

            _connectionFactory = connectionFactory;
            _serializer = serializer;
            _watcher = watcher;
        }

        private void Connect()
        {
            if (_connection == null || !_connection.IsOpen)
            {
                _connection = _connectionFactory.CreateConnection();
            }
        }

        private void DeclareErrorQueue(IModel model)
        {
            if (!_errorQueueDeclared)
            {
                model.QueueDeclare(ErrorQueue,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null);
                
                _errorQueueDeclared = true;
            }
        }

        private void DeclareErrorExchangeAndBindToErrorQueue(IModel model)
        {
            if (!_errorQueueBound)
            {
                model.ExchangeDeclare(ErrorExchange, ExchangeType.Direct, durable: true);
                model.QueueBind(ErrorQueue, ErrorExchange, string.Empty);

                _errorQueueBound = true;
            }
        }

        private void InitializeErrorExchangeAndQueue(IModel model)
        {
            DeclareErrorQueue(model);
            DeclareErrorExchangeAndBindToErrorQueue(model);
        }

        private byte[] CreateErrorMessage(BasicDeliverEventArgs devliverArgs, Exception exception)
        {
            var messageAsString = Encoding.UTF8.GetString(devliverArgs.Body);
            var error = new BurrowError
                            {
                                RoutingKey = devliverArgs.RoutingKey,
                                Exchange = devliverArgs.Exchange,
                                Exception = exception.ToString(),
                                Message = messageAsString,
                                DateTime = DateTime.Now,
                                BasicProperties = new BasicPropertiesWrapper(devliverArgs.BasicProperties)
                            };

            return _serializer.Serialize(error);
        }

        private string CreateConnectionCheckMessage()
        {
            return
                "Please check connection string and that the RabbitMQ Service is running at the specified endpoint.\n" +
                string.Format("\tHostname: '{0}'\n", _connectionFactory.HostName) +
                string.Format("\tVirtualHost: '{0}'\n", _connectionFactory.VirtualHost) +
                string.Format("\tUserName: '{0}'\n", _connectionFactory.UserName) +
                "Failed to write error message to error queue";
        }

        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;

            if (_connection != null)
            {
                _connection.Dispose();
            }
            _disposed = true;
        }

        public void HandleError(BasicDeliverEventArgs deliverEventArgs, Exception exception)
        {
            try
            {
                Connect();

                using (var model = _connection.CreateModel())
                {
                    lock (_channelGate)
                    {
                        InitializeErrorExchangeAndQueue(model);
                    }
                    var messageBody = CreateErrorMessage(deliverEventArgs, exception);
                    var properties = model.CreateBasicProperties();
                    properties.SetPersistent(true);
                    model.BasicPublish(ErrorExchange, string.Empty, properties, messageBody);
                }
            }
            catch (BrokerUnreachableException)
            {
                // thrown if the broker is unreachable during initial creation.
                _watcher.ErrorFormat("ConsumerErrorHandler: cannot connect to Broker.\n" + CreateConnectionCheckMessage());
            }
            catch (OperationInterruptedException interruptedException)
            {
                // thrown if the broker connection is broken during declare or publish.
                _watcher.ErrorFormat(
                    "ConsumerErrorHandler: Broker connection was closed while attempting to publish Error message.\n" +
                    string.Format("Message was: '{0}'\n", interruptedException.Message) +
                    CreateConnectionCheckMessage());
            }
            catch (Exception unexpecctedException)
            {
                _watcher.ErrorFormat("ConsumerErrorHandler: Failed to publish error message\nException is:\n" + unexpecctedException);
            }
        }
    }
}
