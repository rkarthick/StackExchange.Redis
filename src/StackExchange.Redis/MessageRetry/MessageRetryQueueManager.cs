﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace StackExchange.Redis
{
    /// <summary>
    /// 
    /// </summary>
    internal class CommandRetryQueueManager : IDisposable
    { 
        readonly Queue<FailedCommand> queue = new Queue<FailedCommand>();
        int? maxRetryQueueLength;
        CommandFlags RetryOption;
       /// <summary>
       /// 
       /// </summary>
       /// <param name="flags"></param>
       /// <param name="maxRetryQueueLength"></param>
        protected CommandRetryQueueManager(CommandFlags flags, int? maxRetryQueueLength = null)
        {
            this.maxRetryQueueLength = maxRetryQueueLength;
            RetryOption = flags;
        }

        internal int RetryQueueCount => queue.Count;

        /// <summary>
        /// 
        /// </summary>
        private static Func<FailedCommand, bool> FailedMessageHandler;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryHandleFailedCommand(FailedCommand message)
        {
            if (FailedMessageHandler != null && !FailedMessageHandler(message)) return false;
            if ((RetryOption & CommandFlags.RetryIfNotYetSent) == 0 && message.Status == CommandStatus.Sent) return false;

            bool wasEmpty;
            lock (queue)
            {
                int count = queue.Count;
                if (maxRetryQueueLength.HasValue && count >= maxRetryQueueLength)
                {
                    return false;
                }
                wasEmpty = count == 0;
                queue.Enqueue(message);
            }
            if (wasEmpty) StartRetryQueueProcessor();
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void StartRetryQueueProcessor()
        {
            bool startProcessor = false;
            lock (queue)
            {
                startProcessor = queue.Count > 0;
            }
            if (startProcessor) Task.Run(ProcessRetryQueueAsync);
        }

        private async Task ProcessRetryQueueAsync()
        {
            FailedCommand message = null;
            var timeout = message.AsyncTimeoutMilliseconds;
            long messageProcessedCount = 0;
            bool shouldWait = false;
            while (true)
            {
                message = null;
                CheckRetryQueueForTimeouts();
                if (shouldWait) await Task.Delay(1000);
                shouldWait = false;

                lock (queue)
                {
                    if (queue.Count == 0) break; // all done
                    message = queue.Peek();
                    if (!message.IsEndpointAvailable())
                    {
                        shouldWait = true;
                        continue;
                    }
                    message = queue.Dequeue();
                }
                try
                {
                    await message.TryResendAsync();
                }
                catch (Exception ex)
                {
                    HandleException(message, ex);
                }
                messageProcessedCount++;
            }
        }

        internal void HandleException(FailedCommand message, Exception ex)
        {
            var inner = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Failed while retrying on connection restore", ex);
            message.SetExceptionAndComplete(inner, null, onConnectionRestoreRetry: false);
        }

        internal void CheckRetryQueueForTimeouts() // check the head of the backlog queue, consuming anything that looks dead
        {
            lock (queue)
            {
                var now = Environment.TickCount;
                while (queue.Count != 0)
                {
                    var message = queue.Peek();
                    if (!message.HasTimedOut)
                    {
                        break; // not a timeout - we can stop looking
                    }
                    queue.Dequeue();
                    RedisTimeoutException ex = GetTimeoutException(message);
                    HandleException(message, ex);
                }
            }
        }

        // I am not using ExceptionFactory.Timeout as it can cause deadlock while trying to lock writtenawaiting response queue for GetHeadMessages
        internal RedisTimeoutException GetTimeoutException(FailedCommand message)
        {
            var sb = new StringBuilder();
            sb.Append("Timeout while waiting for connectionrestore ").Append(message.Command).Append(" (").Append(Format.ToString(message.TimeoutMilliseconds)).Append("ms)");
            var ex = new RedisTimeoutException(sb.ToString(), message?.Status ?? CommandStatus.Unknown);
            return ex;
        }

        

        private bool disposedValue = false;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="disposing"></param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    lock (queue)
                    {
                        queue.Clear();
                    }
                }
                disposedValue = true;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }
    }
}
