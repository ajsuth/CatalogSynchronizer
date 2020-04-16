﻿// © 2016 Sitecore Corporation A/S. All rights reserved. Sitecore® is a registered trademark of Sitecore Corporation A/S.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Braintree;
using Braintree.Exceptions;
using Microsoft.Extensions.Logging;
using Sitecore.Commerce.Core;
using Sitecore.Commerce.Plugin.ManagedLists;
using Sitecore.Commerce.Plugin.Orders;
using Sitecore.Commerce.Plugin.Payments;
using Sitecore.Framework.Conditions;
using Sitecore.Framework.Pipelines;

namespace Plugin.Sample.Payments.Braintree
{
    /// <summary>
    /// Defines a void canceled order federated paymentBlock.
    /// </summary>
    /// <seealso>
    /// <cref>
    ///   Sitecore.Framework.Pipelines.PipelineBlock{Sitecore.Commerce.Plugin.Payments.Order, 
    ///   Sitecore.Commerce.Plugin.Payments.Order, Sitecore.Commerce.Core.CommercePipelineExecutionContext}
    /// </cref>
    /// </seealso>
    [PipelineDisplayName(PaymentsBraintreeConstants.VoidCancelOrderFederatedPaymentBlock)]
    public class VoidCancelOrderFederatedPaymentBlock : PipelineBlock<Order, Order, CommercePipelineExecutionContext>
    {
        private readonly IPersistEntityPipeline _persistPipeline;

        /// <summary>
        /// Initializes a new instance of the <see cref="VoidCancelOrderFederatedPaymentBlock"/> class.
        /// </summary>
        /// <param name="persistEntityPipeline">The persist entity pipeline.</param>
        public VoidCancelOrderFederatedPaymentBlock(IPersistEntityPipeline persistEntityPipeline)
        {
            _persistPipeline = persistEntityPipeline;
        }

        /// <summary>
        /// Runs the specified argument.
        /// </summary>
        /// <param name="arg">The argument.</param>
        /// <param name="context">The context.</param>
        /// <returns>An order with Federated payment info</returns>
        public override async Task<Order> Run(Order arg, CommercePipelineExecutionContext context)
        {
            Condition.Requires(arg).IsNotNull("The arg can not be null");

            var order = arg;

            if (!order.HasComponent<OnHoldOrderComponent>()
                && !order.Status.Equals(context.GetPolicy<KnownOrderStatusPolicy>().Pending, StringComparison.OrdinalIgnoreCase)
                && !order.Status.Equals(context.GetPolicy<KnownOrderStatusPolicy>().Problem, StringComparison.OrdinalIgnoreCase))
            {
                var expectedStatuses = $"{context.GetPolicy<KnownOrderStatusPolicy>().Pending}, {context.GetPolicy<KnownOrderStatusPolicy>().Problem}, {context.GetPolicy<KnownOrderStatusPolicy>().OnHold}";
                var invalidOrderStateMessage = $"{Name}: Expected order in '{expectedStatuses}' statuses but order was in '{order.Status}' status";
                context.Abort(
                    await context.CommerceContext.AddMessage(
                        context.GetPolicy<KnownResultCodes>().ValidationError,
                        "InvalidOrderState",
                        new object[]
                        {
                            expectedStatuses,
                            order.Status
                        },
                        invalidOrderStateMessage).ConfigureAwait(false),
                    context);
                return arg;
            }

            if (!order.HasComponent<FederatedPaymentComponent>())
            {
                return arg;
            }

            var braintreeClientPolicy = context.GetPolicy<BraintreeClientPolicy>();
            if (!(await braintreeClientPolicy.IsValid(context.CommerceContext).ConfigureAwait(false)))
            {
                return arg;
            }

            try
            {
                var gateway = new BraintreeGateway(braintreeClientPolicy?.Environment, braintreeClientPolicy?.MerchantId, braintreeClientPolicy?.PublicKey, braintreeClientPolicy?.PrivateKey);

                var existingPayment = order.GetComponent<FederatedPaymentComponent>();
                if (existingPayment == null)
                {
                    return arg;
                }

                Result<Transaction> result = gateway.Transaction.Void(existingPayment.TransactionId);
                if (result.IsSuccess())
                {
                    context.Logger.LogInformation($"{Name} - Void Payment succeeded:{existingPayment.Id}");
                    existingPayment.TransactionStatus = result.Target.Status.ToString();
                    await GenerateSalesActivity(order, existingPayment, context).ConfigureAwait(false);
                }
                else
                {
                    var errorMessages = result.Errors.DeepAll().Aggregate(string.Empty, (current, error) => current + ("Error: " + (int) error.Code + " - " + error.Message + "\n"));

                    context.Abort(
                        await context.CommerceContext.AddMessage(
                            context.GetPolicy<KnownResultCodes>().Error,
                            "PaymentVoidFailed",
                            new object[]
                            {
                                existingPayment.TransactionId
                            },
                            $"{Name}. Payment void failed for transaction {existingPayment.TransactionId}: {errorMessages}").ConfigureAwait(false),
                        context);

                    return arg;
                }
            }
            catch (BraintreeException ex)
            {
                await context.CommerceContext.AddMessage(
                    context.GetPolicy<KnownResultCodes>().Error,
                    "PaymentVoidFailed",
                    new object[]
                    {
                        order.Id,
                        ex
                    },
                    $"{Name}. Payment refund failed.").ConfigureAwait(false);
                return arg;
            }

            return arg;
        }

        /// <summary>
        /// Generates the sales activity.
        /// </summary>
        /// <param name="order">The order.</param>
        /// <param name="payment">The payment.</param>
        /// <param name="context">The context.</param>
        /// <returns>A <see cref="Task"/></returns>
        protected virtual async Task GenerateSalesActivity(Order order, PaymentComponent payment, CommercePipelineExecutionContext context)
        {
            var salesActivity = new SalesActivity
            {
                Id = CommerceEntity.IdPrefix<SalesActivity>() + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                ActivityAmount = new Money(payment.Amount.CurrencyCode, 0),
                Customer = new EntityReference
                {
                    EntityTarget = order.EntityComponents.OfType<ContactComponent>().FirstOrDefault()?.CustomerId
                },
                Order = new EntityReference
                {
                    EntityTarget = order.Id,
                    EntityTargetUniqueId = order.UniqueId
                },
                Name = "Void the Federated payment",
                PaymentStatus = context.GetPolicy<KnownSalesActivityStatusesPolicy>().Void
            };

            salesActivity.SetComponent(new ListMembershipsComponent
            {
                Memberships = new List<string>
                {
                    CommerceEntity.ListName<SalesActivity>(),
                    string.Format(CultureInfo.InvariantCulture, context.GetPolicy<KnownOrderListsPolicy>().OrderSalesActivities, order.FriendlyId)
                }
            });

            salesActivity.SetComponent(payment);

            var salesActivities = order.SalesActivity.ToList();
            salesActivities.Add(new EntityReference
            {
                EntityTarget = salesActivity.Id,
                EntityTargetUniqueId = salesActivity.UniqueId
            });
            order.SalesActivity = salesActivities;

            await _persistPipeline.Run(new PersistEntityArgument(salesActivity), context).ConfigureAwait(false);
        }
    }
}
