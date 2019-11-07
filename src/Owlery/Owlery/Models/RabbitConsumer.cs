using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Owlery.Utils;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Owlery.Models
{
    public class RabbitConsumer
    {
        private readonly ConsumerMethod method;
        private readonly IModel model;
        private readonly IServiceProvider serviceProvider;
        private readonly ILogger logger;

        public RabbitConsumer(
            ConsumerMethod method,
            IModel model,
            IServiceProvider serviceProvider,
            ILogger<RabbitConsumer> logger)
        {
            this.method = method;
            this.model = model;
            this.serviceProvider = serviceProvider;
            this.logger = logger;

            this.logger.LogInformation(
                $"Registering method {this.method.Method.Name} in {this.method.ParentType.Name} as consumer" +
                $"{(this.method.PublisherAttributes == null ? "" : " and publisher")}.");

            var rabbitConsumer = new EventingBasicConsumer(model);
            rabbitConsumer.Received += this.RecievedEventHandler;

            var autoAck = this.method.ConsumerAttributes.AcknowledgementType == AcknowledgementType.AutoAck;
            model.BasicConsume(this.method.ConsumerAttributes.QueueName, autoAck, rabbitConsumer);
        }

        public void RecievedEventHandler(object ch, BasicDeliverEventArgs ea)
        {
            this.logger.LogInformation(
                $"Received message {ea.DeliveryTag} from {ea.Exchange} with {ea.RoutingKey}. Invoking " +
                $"{this.method.Method.Name} in {this.method.ParentType.Name}.");

            try
            {
                using (var scope = this.serviceProvider.CreateScope())
                {
                    var consumerService = scope.ServiceProvider.GetRequiredService(this.method.ParentType);

                    var parameters = GetParameterList(this.method, ea, model);
                    var returned = this.method.Method.Invoke(
                        consumerService, parameters);

                    if (this.method.ConsumerAttributes.AcknowledgementType == AcknowledgementType.AckOnInvoke)
                    {
                        this.logger.LogInformation(
                            $"Acknowledging message {ea.DeliveryTag} after invocation.");
                        model.BasicAck(ea.DeliveryTag, false);
                    }

                    if (this.method.PublisherAttributes != null)
                    {
                        this.logger.LogInformation(
                            $"Publishing result of {ea.DeliveryTag} from {this.method.Method.Name} in " +
                            $"{this.method.ParentType.Name} to {this.method.PublisherAttributes.ExchangeName} with " +
                            $"{this.method.PublisherAttributes.RoutingKey}");
                        model.BasicPublish(
                            this.method.PublisherAttributes.ExchangeName,
                            this.method.PublisherAttributes.RoutingKey,
                            null,
                            BodyConverter.ConvertToByteArray(returned));
                    }

                    if (this.method.ConsumerAttributes.AcknowledgementType == AcknowledgementType.AckOnPublish)
                    {
                        this.logger.LogInformation(
                            $"Acknowledging message {ea.DeliveryTag} after publish.");
                        model.BasicAck(ea.DeliveryTag, false);
                    }
                }
            }
            catch (TargetInvocationException exc)
            {
                this.logger.LogError(
                    exc.InnerException,
                    $"Message {ea.DeliveryTag} threw exception when consumer was invoked, will nack.");
                model.BasicNack(ea.DeliveryTag, false, this.method.ConsumerAttributes.NackOnException);
            }
            catch (Exception exc)
            {
                this.logger.LogError(
                    exc,
                    $"Message {ea.DeliveryTag} threw exception, will nack.");
                model.BasicNack(ea.DeliveryTag, false, this.method.ConsumerAttributes.NackOnException);
            }
        }

        private object[] GetParameterList(ConsumerMethod method, BasicDeliverEventArgs eventArgs, IModel model)
        {
            List<object> paramList = new List<object>();
            foreach (var param in method.Method.GetParameters())
            {
                if (param.IsDefined(typeof(FromBodyAttribute), false))
                {
                    paramList.Add(BodyConverter.ConvertFromByteArray(eventArgs.Body, param.ParameterType));
                }
                else if (param.IsDefined(typeof(FromDeliveryTagAttribute), false))
                {
                    paramList.Add(eventArgs.DeliveryTag);
                }
                else if (param.IsDefined(typeof(FromModelAttribute), false))
                {
                    paramList.Add(model);
                }
                else if (param.IsDefined(typeof(FromBasicPropertiesAttribute), false))
                {
                    paramList.Add(eventArgs.BasicProperties);
                }
                else if (param.IsDefined(typeof(FromConsumerTagAttribute), false))
                {
                    paramList.Add(eventArgs.ConsumerTag);
                }
                else if (param.IsDefined(typeof(FromExchangeAttribute), false))
                {
                    paramList.Add(eventArgs.Exchange);
                }
                else if (param.IsDefined(typeof(FromRedeliveredAttribute), false))
                {
                    paramList.Add(eventArgs.Redelivered);
                }
                else if (param.IsDefined(typeof(FromRoutingKeyAttribute), false))
                {
                    paramList.Add(eventArgs.RoutingKey);
                }
            }

            return paramList.ToArray();
        }
    }
}