﻿// -----------------------------------------------------------------------
//   <copyright file="EventStream.cs" company="Asynkron AB">
//       Copyright (C) 2015-2020 Asynkron AB All rights reserved
//   </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Proto.Mailbox;
// ReSharper disable once CheckNamespace
namespace Proto
{
    public class EventStream : EventStream<object>
    {
        private readonly ILogger _logger = Log.CreateLogger<EventStream>();

        internal EventStream() : this(TimeSpan.Zero, 0){}
        
        internal EventStream(TimeSpan throttleInterval,int throttleCount)
        {
            var lastTick = 0L;
            var messages = 0L;
            var throttled = false;
            Subscribe<DeadLetterEvent>(
                dl =>
                {
                    if (throttleInterval != TimeSpan.Zero)
                    {
                        if (DateTimeOffset.Now.Ticks > lastTick + throttleInterval.Ticks)
                        {
                            if (throttled)
                            {
                                _logger.LogInformation(
                                    "[DeadLetter] Resuming DeadLetter event logging..."
                                );
                            }
                            throttled = false;
                            
                        }
                        else if (!throttled)
                        {
                            var res = Interlocked.Increment(ref messages);
                            if (res >= throttleCount)
                            {
                                throttled = true;
                                Interlocked.Exchange(ref messages, 0L);
                                _logger.LogInformation(
                                    "[DeadLetter] Throttling DeadLetter event logging..."
                                );
                            }
                        }

                        lastTick = DateTimeOffset.Now.Ticks;
                    }

                    if (!throttled)
                    {
                        _logger.LogInformation(
                            "[DeadLetter] could not deliver '{MessageType}:{Message}' to '{Target}' from '{Sender}'",
                            dl.Message.GetType().Name,
                            dl.Message,
                            dl.Pid.ToShortString(),
                            dl.Sender?.ToShortString()
                        );
                    }
                }
            );
        }
    }

    /// <summary>
    ///     Global event stream of a specific message type
    /// </summary>
    /// <typeparam name="T">Message type</typeparam>
    [PublicAPI]
    public class EventStream<T>
    {
        private readonly ILogger _logger = Log.CreateLogger<EventStream<T>>();

        private readonly ConcurrentDictionary<Guid, Subscription<T>> _subscriptions =
            new ConcurrentDictionary<Guid, Subscription<T>>();

        internal EventStream()
        {
        }

        /// <summary>
        ///     Subscribe to the specified message type
        /// </summary>
        /// <param name="action">Synchronous message handler</param>
        /// <param name="dispatcher">Optional: the dispatcher, will use <see cref="Dispatchers.SynchronousDispatcher" /> by default</param>
        /// <returns>A new subscription that can be used to unsubscribe</returns>
        public Subscription<T> Subscribe(Action<T> action, IDispatcher? dispatcher = null)
        {
            var sub = new Subscription<T>(
                this,
                dispatcher ?? Dispatchers.SynchronousDispatcher,
                x =>
                {
                    action(x);
                    return Task.CompletedTask;
                }
            );
            _subscriptions.TryAdd(sub.Id, sub);
            return sub;
        }

        /// <summary>
        ///     Subscribe to the specified message type with an asynchronous handler
        /// </summary>
        /// <param name="action">Asynchronous message handler</param>
        /// <param name="dispatcher">Optional: the dispatcher, will use <see cref="Dispatchers.SynchronousDispatcher" /> by default</param>
        /// <returns>A new subscription that can be used to unsubscribe</returns>
        public Subscription<T> Subscribe(Func<T, Task> action, IDispatcher? dispatcher = null)
        {
            var sub = new Subscription<T>(this, dispatcher ?? Dispatchers.SynchronousDispatcher, action);
            _subscriptions.TryAdd(sub.Id, sub);
            return sub;
        }

        /// <summary>
        ///     Subscribe to the specified message type, which is a derived type from <see cref="T" />
        /// </summary>
        /// <param name="action">Synchronous message handler</param>
        /// <param name="dispatcher">Optional: the dispatcher, will use <see cref="Dispatchers.SynchronousDispatcher" /> by default</param>
        /// <returns>A new subscription that can be used to unsubscribe</returns>
        public Subscription<T> Subscribe<TMsg>(Action<TMsg> action, IDispatcher? dispatcher = null) where TMsg : T
        {
            var sub = new Subscription<T>(
                this,
                dispatcher ?? Dispatchers.SynchronousDispatcher,
                msg =>
                {
                    if (msg is TMsg typed)
                    {
                        action(typed);
                    }

                    return Task.CompletedTask;
                }
            );

            _subscriptions.TryAdd(sub.Id, sub);
            return sub;
        }


        public Subscription<T> Subscribe<TMsg>(ISenderContext context, params PID[] pids) where TMsg : T
        {
            var sub = new Subscription<T>(
                this,
                Dispatchers.SynchronousDispatcher,
                msg =>
                {
                    if (msg is TMsg)
                    {
                        foreach (var pid in pids)
                        {
                            context.Send(pid, msg);
                        }
                    }

                    return Task.CompletedTask;
                }
            );

            _subscriptions.TryAdd(sub.Id, sub);
            return sub;
        }

        /// <summary>
        ///     Subscribe to the specified message type, which is a derived type from <see cref="T" />
        /// </summary>
        /// <param name="action">Asynchronous message handler</param>
        /// <param name="dispatcher">Optional: the dispatcher, will use <see cref="Dispatchers.SynchronousDispatcher" /> by default</param>
        /// <returns>A new subscription that can be used to unsubscribe</returns>
        public Subscription<T> Subscribe<TMsg>(Func<TMsg, Task> action, IDispatcher? dispatcher = null) where TMsg : T
        {
            var sub = new Subscription<T>(
                this,
                dispatcher ?? Dispatchers.SynchronousDispatcher,
                msg => msg is TMsg typed ? action(typed) : Task.CompletedTask
            );

            _subscriptions.TryAdd(sub.Id, sub);
            return sub;
        }

        /// <summary>
        ///     Publish a message to the event stream
        /// </summary>
        /// <param name="msg">A message to publish</param>
        public void Publish(T msg)
        {
            foreach (var sub in _subscriptions.Values)
            {
                sub.Dispatcher.Schedule(
                    () =>
                    {
                        try
                        {
                            sub.Action(msg);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(0, ex, "Exception has occurred when publishing a message.");
                        }

                        return Task.CompletedTask;
                    }
                );
            }
        }

        /// <summary>
        ///     Remove a subscription by id
        /// </summary>
        /// <param name="id">Subscription id</param>
        public void Unsubscribe(Guid id) => _subscriptions.TryRemove(id, out _);

        /// <summary>
        ///     Remove a subscription
        /// </summary>
        /// <param name="subscription"> A subscription to remove</param>
        public void Unsubscribe(Subscription<T>? subscription)
        {
            if (subscription != null)
            {
                Unsubscribe(subscription.Id);
            }
        }
    }

    public class Subscription<T>
    {
        private readonly EventStream<T> _eventStream;

        public Subscription(EventStream<T> eventStream, IDispatcher dispatcher, Func<T, Task> action)
        {
            Id = Guid.NewGuid();
            _eventStream = eventStream;
            Dispatcher = dispatcher;
            Action = action;
        }

        public Guid Id { get; }
        public IDispatcher Dispatcher { get; }
        public Func<T, Task> Action { get; }

        public void Unsubscribe() => _eventStream.Unsubscribe(Id);
    }
}