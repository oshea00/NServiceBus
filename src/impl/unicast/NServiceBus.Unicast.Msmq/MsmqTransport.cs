using System;
using System.Collections.Generic;
using System.Messaging;
using System.Transactions;
using Common.Logging;
using Utils;
using NServiceBus.Serialization;
using System.Xml.Serialization;
using System.IO;

namespace NServiceBus.Unicast.Transport.Msmq
{
	/// <summary>
	/// An MSMQ implementation of <see cref="ITransport"/> for use with
	/// NServiceBus.
	/// </summary>
	/// <remarks>
	/// A transport is used by NServiceBus as a high level abstraction from the 
	/// underlying messaging service being used to transfer messages.
	/// </remarks>
    public class MsmqTransport : ITransport
    {
        #region config info

		/// <summary>
		/// Sets the path to the queue the transport will read from.
		/// Only specify the name of the queue - msmq specific address not required.
		/// When using MSMQ v3, only local queues are supported.
		/// </summary>
        public virtual string InputQueue
        {
            set
            {
                string path = GetFullPath(value);
                MessageQueue q = new MessageQueue(path);

                SetLocalQueue(q);

                if (this.purgeOnStartup)
                    this.queue.Purge();
            }
        }

		/// <summary>
		/// Sets the path to the queue the transport will transfer
		/// errors to.
		/// </summary>
        public virtual string ErrorQueue
        {
            set
            {
                errorQueuePath = value;
                this.errorQueue = new MessageQueue(GetFullPath(value));
            }
            get
            {
                return errorQueuePath;
            }
        }

	    private string errorQueuePath;

        private bool isTransactional;

		/// <summary>
		/// Sets whether or not the transport is transactional.
		/// </summary>
        public virtual bool IsTransactional
        {
            set { this.isTransactional = value; }
        }

        private bool skipDeserialization;

		/// <summary>
		/// Sets whether or not the transport should deserialize
		/// the body of the message placed on the queue.
		/// </summary>
        public virtual bool SkipDeserialization
        {
            set { skipDeserialization = value; }
        }

        private bool purgeOnStartup = false;

		/// <summary>
		/// Sets whether or not the transport should purge the input
		/// queue when it is started.
		/// </summary>
        public virtual bool PurgeOnStartup
        {
            set
            {
                purgeOnStartup = value;

                if (this.purgeOnStartup && this.queue != null)
                    this.queue.Purge();
            }
        }

	    private int maxRetries = 5;

        /// <summary>
        /// Sets the maximum number of times a message will be retried
        /// when an exception is thrown as a result of handling the message.
        /// This value is only relevant when <see cref="IsTransactional"/> is true.
        /// </summary>
        /// <remarks>
        /// Default value is 5.
        /// </remarks>
        public virtual int MaxRetries
	    {
                get { return maxRetries; }
	        set { maxRetries = value; }
	    }

	    private int secondsToWaitForMessage = 10;

        /// <summary>
        /// Sets the maximum interval of time for when a thread thinks there is a message in the queue
        /// that it tries to receive, until it gives up.
        /// 
        /// Default value is 10.
        /// </summary>
        public virtual int SecondsToWaitForMessage
        {
            get { return secondsToWaitForMessage; }
            set { secondsToWaitForMessage = value; }
        }

	    private TimeSpan transactionTimeout;

        /// <summary>
        /// Property for getting/setting the period of time when the transaction times out.
        /// Only relevant when <see cref="IsTransactional"/> is set to true.
        /// </summary>
        public virtual TimeSpan TransactionTimeout
        {
            get { return transactionTimeout; }
            set { transactionTimeout = value; }
        }

	    private IsolationLevel isolationLevel;
        
        /// <summary>
        /// Property for getting/setting the isolation level of the transaction scope.
        /// Only relevant when <see cref="IsTransactional"/> is set to true.
        /// </summary>
	    public virtual IsolationLevel IsolationLevel
	    {
            get { return isolationLevel; }
            set { this.isolationLevel = value; }
	    }

	    private IMessageSerializer messageSerializer;

        /// <summary>
        /// Sets the object which will be used to serialize and deserialize messages.
        /// </summary>
        public virtual IMessageSerializer MessageSerializer
	    {
            set { this.messageSerializer = value; }
	    }

        #endregion

        #region ITransport Members

        /// <summary>
        /// Gets/sets the number of concurrent threads that should be
        /// created for processing the queue.
        /// 
        /// Get returns the actual number of running worker threads, which may
        /// be different than the originally configured value.
        /// 
        /// When used as a setter, this value will be used by the <see cref="Start"/>
        /// method only and will have no effect if called afterwards.
        /// 
        /// To change the number of worker threads at runtime, call <see cref="ChangeNumberOfWorkerThreads"/>.
        /// </summary>
        public virtual int NumberOfWorkerThreads
        {
            get
            {
                lock (this.workerThreads)
                    return this.workerThreads.Count;
            }
            set
            {
                _numberOfWorkerThreads = value;
            }
        }
        private int _numberOfWorkerThreads;


		/// <summary>
		/// Event raised when a message has been received in the input queue.
		/// </summary>
        public event EventHandler<TransportMessageReceivedEventArgs> TransportMessageReceived;

		/// <summary>
		/// Gets the address of the input queue.
		/// </summary>
        public string Address
        {
            get
            {
                return GetIndependentAddressForQueue(this.queue);
            }
        }

		/// <summary>
		/// Sets a list of the message types the transport will receive.
		/// </summary>
        public virtual IList<Type> MessageTypesToBeReceived
        {
            set { this.messageSerializer.Initialize(GetExtraTypes(value)); }
        }

	    public void ChangeNumberOfWorkerThreads(int targetNumberOfWorkerThreads)
        {
            lock (this.workerThreads)
            {
                int current = this.workerThreads.Count;

                if (targetNumberOfWorkerThreads == current)
                    return;

                if (targetNumberOfWorkerThreads < current)
                {
                    for (int i = targetNumberOfWorkerThreads; i < current; i++)
                        this.workerThreads[i].Stop();

                    return;
                }

                if (targetNumberOfWorkerThreads > current)
                {
                    for (int i = current; i < targetNumberOfWorkerThreads; i++)
                        this.AddWorkerThread().Start();

                    return;
                }
            }
        }

	    /// <summary>
		/// Starts the transport.
		/// </summary>
        public void Start()
        {
            //don't purge on startup here

            for (int i = 0; i < this._numberOfWorkerThreads; i++)
                this.AddWorkerThread().Start();
        }

		/// <summary>
		/// Re-queues a message for processing at another time.
		/// </summary>
		/// <param name="m">The message to process later.</param>
		/// <remarks>
		/// This method will place the message onto the back of the queue
		/// which may break message ordering.
		/// </remarks>
        public void ReceiveMessageLater(TransportMessage m)
        {
            this.Send(m, this.Address);
        }

		/// <summary>
		/// Sends a message to the specified destination.
		/// </summary>
		/// <param name="m">The message to send.</param>
		/// <param name="destination">The address of the destination to send the message to.</param>
        public void Send(TransportMessage m, string destination)
        {
		    string address = GetFullPath(destination);

            using (MessageQueue q = new MessageQueue(address, QueueAccessMode.Send))
            {
                Message toSend = new Message();

                if (m.Body == null && m.BodyStream != null)
                    toSend.BodyStream = m.BodyStream;
                else
                    this.messageSerializer.Serialize(m.Body, toSend.BodyStream);

                if (m.CorrelationId != null)
                    toSend.CorrelationId = m.CorrelationId;

                toSend.Recoverable = m.Recoverable;
                toSend.ResponseQueue = new MessageQueue(GetFullPath(m.ReturnAddress));
                toSend.Label = m.IdForCorrelation + ":" + m.WindowsIdentityName;

                if (m.TimeToBeReceived < MessageQueue.InfiniteTimeout)
                    toSend.TimeToBeReceived = m.TimeToBeReceived;

                if (m.Headers != null && m.Headers.Count > 0)
                {
                    MemoryStream stream = new MemoryStream();
                    headerSerializer.Serialize(stream, m.Headers);
                    toSend.Extension = stream.GetBuffer();
                }

                q.Send(toSend, this.GetTransactionTypeForSend());

                m.Id = toSend.Id;
            }
        }

        public int GetNumberOfPendingMessages()
        {
            MSMQ.MSMQManagementClass qMgmt = new MSMQ.MSMQManagementClass();
            object machine = Environment.MachineName;
            object missing = Type.Missing;
            object formatName = this.queue.FormatName;

            qMgmt.Init(ref machine, ref missing, ref formatName);
            return qMgmt.MessageCount;
        }

        #endregion

        #region helper methods

        private WorkerThread AddWorkerThread()
        {
            lock (this.workerThreads)
            {
                WorkerThread result = new WorkerThread(this.Receive);

                this.workerThreads.Add(result);

                result.Stopped += delegate(object sender, EventArgs e)
                                      {
                                          WorkerThread wt = sender as WorkerThread;
                                          lock (this.workerThreads)
                                              this.workerThreads.Remove(wt);
                                      };

                return result;
            }
        }

		/// <summary>
		/// Waits for a message to become available on the input queue
		/// and then receives it.
		/// </summary>
		/// <remarks>
		/// If the queue is transactional the receive operation will be wrapped in a 
		/// transaction.
		/// </remarks>
        private void Receive()
        {
            try
            {
                this.queue.Peek(TimeSpan.FromMilliseconds(1000));
            }
            catch (MessageQueueException mqe)
            {
                if (mqe.MessageQueueErrorCode == MessageQueueErrorCode.IOTimeout)
                    return;

                throw;
            }

            this.OnStartedMessageProcessing();

		    needToAbort = false;

            try
            {
                if (this.isTransactional)
                    new TransactionWrapper().RunInTransaction(this.ReceiveFromQueue, isolationLevel, transactionTimeout);
                else
                    this.ReceiveFromQueue();
            }
            catch(AbortHandlingCurrentMessageException)
            {
                //in case AbortHandlingCurrentMessage occurred
            }

            this.OnFinishedMessageProcessing();
        }

	    /// <summary>
		/// Receives a message from the input queue.
		/// </summary>
		/// <remarks>
		/// If a message is received the <see cref="TransportMessageReceived"/> event will be raised.
		/// </remarks>
        public void ReceiveFromQueue()
        {
	        Message m = new Message();

            try
            {
                m = this.queue.Receive(TimeSpan.FromSeconds(SecondsToWaitForMessage), this.GetTransactionTypeForReceive());

                TransportMessage result = Convert(m);

                if (this.skipDeserialization)
                    result.BodyStream = m.BodyStream;
                else
                {
                    try
                    {
                        result.Body = Extract(m);
                    }
                    catch (Exception e)
                    {
                        logger.Error("Could not extract message data.", e);

                        MoveToErrorQueue(m);

                        return; // deserialization failed - no reason to try again, so don't throw
                    }
                }

                if (this.TransportMessageReceived != null)
                    this.TransportMessageReceived(this, new TransportMessageReceivedEventArgs(result));

                this.failuresPerMessage.Remove(m.Id);
            }
            catch (MessageQueueException mqe)
            {
                if (mqe.MessageQueueErrorCode == MessageQueueErrorCode.IOTimeout)
                    return;

                throw;
            }
            catch
            {
                if (this.isTransactional)
                    lock (this.failuresPerMessage)
                    {
                        if (!this.failuresPerMessage.ContainsKey(m.Id))
                            this.failuresPerMessage[m.Id] = 1;
                        else
                            this.failuresPerMessage[m.Id] = this.failuresPerMessage[m.Id] + 1;

                        if (this.failuresPerMessage[m.Id] == this.maxRetries)
                        {
                            this.failuresPerMessage.Remove(m.Id);

                            MoveToErrorQueue(m);

                            return;
                        }
                    }

                throw;
            }

            if (needToAbort)
                throw new AbortHandlingCurrentMessageException();

            return;
        }

	    protected void MoveToErrorQueue(Message m)
	    {
	        if (this.errorQueue != null)
                this.errorQueue.Send(m, MessageQueueTransactionType.Single);
	    }

	    public void AbortHandlingCurrentMessage()
        {
            needToAbort = true;
        }

		/// <summary>
		/// Checks whether or not a queue is local by its path.
		/// </summary>
		/// <param name="value">The path to the queue to check.</param>
		/// <returns>true if the queue is local, otherwise false.</returns>
        public static bool QueueIsLocal(string value)
        {
            string machineName = Environment.MachineName.ToLower();

            value = value.ToLower().Replace(PREFIX.ToLower(), "");
            int index = value.IndexOf('\\');

            string queueMachineName = value.Substring(0, index).ToLower();

            return (machineName == queueMachineName || queueMachineName == "localhost" || queueMachineName == ".");
        }

		/// <summary>
		/// Converts an MSMQ <see cref="Message"/> into an NServiceBus message.
		/// </summary>
		/// <param name="m">The MSMQ message to convert.</param>
		/// <returns>An NServiceBus message.</returns>
        public TransportMessage Convert(Message m)
        {
            TransportMessage result = new TransportMessage();
            result.Id = m.Id;
            result.CorrelationId = (m.CorrelationId == "00000000-0000-0000-0000-000000000000\\0" ? null : m.CorrelationId);
            result.Recoverable = m.Recoverable;
            result.TimeToBeReceived = m.TimeToBeReceived;

            result.ReturnAddress = GetIndependentAddressForQueue(m.ResponseQueue);

            if (m.Label != null)
            {
                string[] arr = m.Label.Split(':');
                result.IdForCorrelation = arr[0];
                result.WindowsIdentityName = arr[1];
            }

            if (result.IdForCorrelation == null || result.IdForCorrelation == string.Empty)
                result.IdForCorrelation = result.Id;

            if (m.Extension != null)
                if (m.Extension.Length > 0)
                {
                    MemoryStream stream = new MemoryStream(m.Extension);
                    object o = headerSerializer.Deserialize(stream);
                    result.Headers = o as List<HeaderInfo>;
                }

		    return result;
        }

        /// <summary>
        /// Extracts the messages from an MSMQ <see cref="Message"/>.
        /// </summary>
        /// <param name="message">The MSMQ message to extract from.</param>
        /// <returns>An array of handleable messages.</returns>
        private IMessage[] Extract(Message message)
        {
            return this.messageSerializer.Deserialize(message.BodyStream);
        }

		/// <summary>
		/// Gets the transaction type to use when receiving a message from the queue.
		/// </summary>
		/// <returns>The transaction type to use.</returns>
        private MessageQueueTransactionType GetTransactionTypeForReceive()
        {
            if (this.isTransactional)
                return MessageQueueTransactionType.Automatic;
            else
                return MessageQueueTransactionType.None;
        }

		/// <summary>
		/// Gets the transaction type to use when sending a message.
		/// </summary>
		/// <returns>The transaction type to use.</returns>
        private MessageQueueTransactionType GetTransactionTypeForSend()
        {
            if (this.isTransactional)
            {
                if (Transaction.Current != null)
                    return MessageQueueTransactionType.Automatic;
                else
                    return MessageQueueTransactionType.Single;
            }
            else
                return MessageQueueTransactionType.Single;
        }

		/// <summary>
		/// Sets the queue on the transport to the specified MSMQ queue.
		/// </summary>
		/// <param name="q">The MSMQ queue to set.</param>
        private void SetLocalQueue(MessageQueue q)
        {
            //q.MachineName = Environment.MachineName; // just in case we were given "localhost"
            if (!q.Transactional)
                throw new ArgumentException("Queue must be transactional (" + q.Path + ").");
            else
                this.queue = q;

            MessagePropertyFilter mpf = new MessagePropertyFilter();
            mpf.SetAll();

            this.queue.MessageReadPropertyFilter = mpf;
        }

		/// <summary>
		/// Get a list of serializable types from the list of types provided.
		/// </summary>
		/// <param name="value">A list of types process.</param>
		/// <returns>A list of serializable types.</returns>
        private static Type[] GetExtraTypes(IEnumerable<Type> value)
        {
            List<Type> types = new List<Type>(value);
            if (!types.Contains(typeof(List<object>)))
                types.Add(typeof(List<object>));

            return types.ToArray();
        }

        #endregion

        #region static conversion methods

        ///// <summary>
        ///// Resolves a destination MSMQ queue address.
        ///// </summary>
        ///// <param name="destination">The MSMQ address to resolve.</param>
        ///// <returns>The direct format name of the queue.</returns>
        //public static string Resolve(string destination)
        //{
        //    string dest = destination.ToLower().Replace(PREFIX.ToLower(), "");

        //    string[] arr = dest.Split('\\');
        //    if (arr.Length == 1)
        //        dest = Environment.MachineName + "\\private$\\" + dest;

        //    MessageQueue q = new MessageQueue(dest);
        //    if (q.MachineName.ToLower() == Environment.MachineName.ToLower())
        //        q.MachineName = Environment.MachineName;

        //    return PREFIX + q.Path;
        //}

        /// <summary>
        /// Turns a '@' separated value into a full msmq path.
        /// Format is 'queue@machine'.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static string GetFullPath(string value)
        {
            string[] arr = value.Split('@');

            string queue = arr[0];
            string machine = Environment.MachineName;

            if (arr.Length == 2)
                if (arr[1] != "." && arr[1].ToLower() != "localhost")
                    machine = arr[1];

            return PREFIX + machine + "\\private$\\" + queue;
        }

        public static string GetIndependentAddressForQueue(MessageQueue q)
        {
            if (q == null)
                return null;

            string[] arr = q.FormatName.Split('\\');
            string queueName = arr[arr.Length - 1];

            int directPrefixIndex = arr[0].IndexOf(DIRECTPREFIX);
            if (directPrefixIndex >= 0)
            {
                return queueName + '@' + arr[0].Substring(directPrefixIndex + DIRECTPREFIX.Length);
            }

            try
            {
                // the pessimistic approach failed, try the optimistic approach
                arr = q.QueueName.Split('\\');
                queueName = arr[arr.Length - 1];
                return queueName + '@' + q.MachineName;
            }
            catch
            {
                throw new Exception(string.Concat("MessageQueueException: '",
                DIRECTPREFIX, "' is missing. ",
                "FormatName='", q.FormatName, "'"));
            }


        }

        #endregion

        #region Events

	    public event EventHandler StartedMessageProcessing;
	    public event EventHandler FinishedMessageProcessing;

        private void OnStartedMessageProcessing()
        {
            if (this.StartedMessageProcessing != null)
                this.StartedMessageProcessing(this, null);
        }

        private void OnFinishedMessageProcessing()
        {
            if (this.FinishedMessageProcessing != null)
                this.FinishedMessageProcessing(this, null);
        }
        #endregion

        #region members

        private static readonly string DIRECTPREFIX = "DIRECT=OS:";
        private readonly static string PREFIX = "FormatName:" + DIRECTPREFIX;

        private MessageQueue queue;
        private MessageQueue errorQueue;
        private readonly IList<WorkerThread> workerThreads = new List<WorkerThread>();

        /// <summary>
        /// Accessed by multiple threads - lock before using.
        /// </summary>
	    private readonly IDictionary<string, int> failuresPerMessage = new Dictionary<string, int>();

	    [ThreadStatic] 
        private static volatile bool needToAbort;

        private static readonly ILog logger = LogManager.GetLogger(typeof (MsmqTransport));

        private XmlSerializer headerSerializer = new XmlSerializer(typeof(List<HeaderInfo>));
        #endregion

        #region IDisposable Members

		/// <summary>
		/// Stops all worker threads and disposes the MSMQ queue.
		/// </summary>
        public void Dispose()
        {
            lock (this.workerThreads)
                for (int i = 0; i < workerThreads.Count; i++)
                    this.workerThreads[i].Stop();

            this.queue.Dispose();
        }

        #endregion
    }
}
