﻿using System;
using System.Net.Sockets;
using System.Threading;

namespace Telepathy
{
    // need a thread safe helper class for connecting state
    // (can't pass a 'volatile bool' as ref into the ReceiveThread)
    class SafeBool
    {
        bool _Value;
        public bool Value
        {
            get
            {
                lock (this) { return _Value; }
            }
            set
            {
                lock (this) { _Value = value; }
            }
        }

        public SafeBool(bool value) { _Value = value; }
    }

    public class Client : Common
    {
        // events to hook into
        // => OnData uses ArraySegment for allocation free receives later
        public Action OnConnected;
        public Action<ArraySegment<byte>> OnData;
        public Action OnDisconnected;

        public TcpClient client;
        Thread receiveThread;

        // TcpClient.Connected doesn't check if socket != null, which
        // results in NullReferenceExceptions if connection was closed.
        // -> let's check it manually instead
        public bool Connected => client != null &&
                                 client.Client != null &&
                                 client.Client.Connected;

        // TcpClient has no 'connecting' state to check. We need to keep track
        // of it manually.
        // -> checking 'thread.IsAlive && !Connected' is not enough because the
        //    thread is alive and connected is false for a short moment after
        //    disconnecting, so this would cause race conditions.
        // -> we use a threadsafe bool wrapper so that ThreadFunction can remain
        //    static (it needs a common lock)
        // => Connecting is true from first Connect() call in here, through the
        //    thread start, until TcpClient.Connect() returns. Simple and clear.
        // => bools are atomic according to
        //    https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/variables
        //    made volatile so the compiler does not reorder access to it
        //
        // IMPORTANT: we use an THREAD SAFE OBJECT so that we can pass the
        //            reference to ReceiveThread, and so that we can CREATE A
        //            NEW ONE each time we connect, and never mess with an old
        //            thread's connecting field.
        //            => fixes flaky ReconnectTest and SpamConnectTest!
        SafeBool _Connecting = new SafeBool(false);
        public bool Connecting => _Connecting.Value;

        // thread safe pipe for received messages
        MagnificentReceivePipe receivePipe;

        // thread safe pipe to send messages from main thread to send thread
        MagnificentSendPipe sendPipe;

        // ManualResetEvent to wake up the send thread. better than Thread.Sleep
        // -> call Set() if everything was sent
        // -> call Reset() if there is something to send again
        // -> call WaitOne() to block until Reset was called
        ManualResetEvent sendPending;

        // constructor
        public Client(int MaxMessageSize) : base(MaxMessageSize) {}

        // the thread function
        // STATIC to avoid sharing state!
        // => ReconnectTest() previously had a bug where 'client' could be null
        //    since we the member variable between threads. that was terrible.
        //    passing it as parameter means it will never be null!
        static void ReceiveThreadFunction(TcpClient client,
                                          string ip, int port,
                                          int MaxMessageSize, bool NoDelay, int SendTimeout, int QueueLimit,
                                          MagnificentSendPipe sendPipe,
                                          MagnificentReceivePipe receivePipe,
                                          ManualResetEvent sendPending,
                                          SafeBool Connecting)
        {
            Thread sendThread = null;

            // absolutely must wrap with try/catch, otherwise thread
            // exceptions are silent
            try
            {
                // connect (blocking)
                client.Connect(ip, port);
                Connecting.Value = false;

                // set socket options after the socket was created in Connect()
                // (not after the constructor because we clear the socket there)
                client.NoDelay = NoDelay;
                client.SendTimeout = SendTimeout;

                // start send thread only after connected
                // IMPORTANT: DO NOT SHARE STATE ACROSS MULTIPLE THREADS!
                sendThread = new Thread(() => { ThreadFunctions.SendLoop(0, client, sendPipe, sendPending); });
                sendThread.IsBackground = true;
                sendThread.Start();

                // run the receive loop
                // IMPORTANT: DO NOT SHARE STATE ACROSS MULTIPLE THREADS!
                ThreadFunctions.ReceiveLoop(0, client, MaxMessageSize, receivePipe, QueueLimit);
            }
            catch (SocketException exception)
            {
                // this happens if (for example) the ip address is correct
                // but there is no server running on that ip/port
                Log.Info("Client Recv: failed to connect to ip=" + ip + " port=" + port + " reason=" + exception);

                // set 'Disconnected' event to receive pipe so that the caller
                // knows that the Connect failed. otherwise they will never know
                receivePipe.SetDisconnected();
            }
            catch (ThreadInterruptedException)
            {
                // expected if Disconnect() aborts it
            }
            catch (ThreadAbortException)
            {
                // expected if Disconnect() aborts it
            }
            catch (ObjectDisposedException)
            {
                // expected if Disconnect() aborts it and disposed the client
                // while ReceiveThread is in a blocking Connect() call
            }
            catch (Exception exception)
            {
                // something went wrong. probably important.
                Log.Error("Client Recv Exception: " + exception);
            }

            // sendthread might be waiting on ManualResetEvent,
            // so let's make sure to end it if the connection
            // closed.
            // otherwise the send thread would only end if it's
            // actually sending data while the connection is
            // closed.
            sendThread?.Interrupt();

            // Connect might have failed. thread might have been closed.
            // let's reset connecting state no matter what.
            Connecting.Value = false;

            // if we got here then we are done. ReceiveLoop cleans up already,
            // but we may never get there if connect fails. so let's clean up
            // here too.
            client?.Close();
        }

        public void Connect(string ip, int port)
        {
            // not if already started
            if (Connecting || Connected)
            {
                Log.Warning("Telepathy Client can not create connection because an existing connection is connecting or connected");
                return;
            }

            // We are connecting from now until Connect succeeds or fails
            // => create a new connecting field each time.
            //    we pass it to ReceiveThread and DO NOT EVER want to mess with
            //    an old thread's value
            _Connecting = new SafeBool(true);

            // create a TcpClient with perfect IPv4, IPv6 and hostname resolving
            // support.
            //
            // * TcpClient(hostname, port): works but would connect (and block)
            //   already
            // * TcpClient(AddressFamily.InterNetworkV6): takes Ipv4 and IPv6
            //   addresses but only connects to IPv6 servers (e.g. Telepathy).
            //   does NOT connect to IPv4 servers (e.g. Mirror Booster), even
            //   with DualMode enabled.
            // * TcpClient(): creates IPv4 socket internally, which would force
            //   Connect() to only use IPv4 sockets.
            //
            // => the trick is to clear the internal IPv4 socket so that Connect
            //    resolves the hostname and creates either an IPv4 or an IPv6
            //    socket as needed (see TcpClient source)
            client = new TcpClient(); // creates IPv4 socket
            client.Client = null; // clear internal IPv4 socket until Connect()

            // create pipes with max message size for pooling
            // => create new pipes every time!
            //    if an old receive thread is still finishing up, it might still
            //    be using the old pipes. we don't want to risk any old data for
            //    our new connect here.
            // -> calling this in Disconnect isn't smart because the caller may
            //    still want to process all the latest messages afterwards
            receivePipe = new MagnificentReceivePipe(MaxMessageSize);
            sendPipe = new MagnificentSendPipe(MaxMessageSize);

            // create a new ManualResetEvent each time too.
            // do not ever want to mess with an old thread's event
            sendPending = new ManualResetEvent(false);

            // client.Connect(ip, port) is blocking. let's call it in the thread
            // and return immediately.
            // -> this way the application doesn't hang for 30s if connect takes
            //    too long, which is especially good in games
            // -> this way we don't async client.BeginConnect, which seems to
            //    fail sometimes if we connect too many clients too fast
            receiveThread = new Thread(() => {
                ReceiveThreadFunction(client, ip, port, MaxMessageSize, NoDelay, SendTimeout, QueueLimit, sendPipe, receivePipe, sendPending, _Connecting);
            });
            receiveThread.IsBackground = true;
            receiveThread.Start();
        }

        public void Disconnect()
        {
            // only if started
            if (Connecting || Connected)
            {
                // close client
                client.Close();

                // wait until thread finished. this is the only way to guarantee
                // that we can call Connect() again immediately after Disconnect
                // -> calling .Join would sometimes wait forever, e.g. when
                //    calling Disconnect while trying to connect to a dead end
                receiveThread?.Interrupt();

                // we interrupted the receive Thread, so we can't guarantee that
                // connecting was reset. let's do it manually.
                _Connecting.Value = false;

                // clear send pipe. no need to hold on to elements.
                // (unlike receiveQueue, which is still needed to process the
                //  latest Disconnected message, etc.)
                sendPipe.Clear();

                // let go of this one completely. the thread ended, no one uses
                // it anymore and this way Connected is false again immediately.
                client = null;
            }
        }

        // send message to server using socket connection.
        // arraysegment for allocation free sends later.
        // -> the segment's array is only used until Send() returns!
        public bool Send(ArraySegment<byte> message)
        {
            if (Connected)
            {
                // respect max message size to avoid allocation attacks.
                if (message.Count <= MaxMessageSize)
                {
                    // check send pipe limit
                    if (sendPipe.Count < QueueLimit)
                    {
                        // add to thread safe send pipe and return immediately.
                        // calling Send here would be blocking (sometimes for long
                        // times if other side lags or wire was disconnected)
                        sendPipe.Enqueue(message);
                        sendPending.Set(); // interrupt SendThread WaitOne()
                        return true;
                    }
                    // disconnect if send queue gets too big.
                    // -> avoids ever growing queue memory if network is slower
                    //    than input
                    // -> avoids ever growing latency as well
                    //
                    // note: while SendThread always grabs the WHOLE send queue
                    //       immediately, it's still possible that the sending
                    //       blocks for so long that the send queue just gets
                    //       way too big. have a limit - better safe than sorry.
                    else
                    {
                        // log the reason
                        Log.Warning($"Client.Send: sendPipe reached limit of {QueueLimit}. This can happen if we call send faster than the network can process messages. Disconnecting to avoid ever growing memory & latency.");

                        // just close it. send thread will take care of the rest.
                        client.Close();
                        return false;
                    }
                }
                Log.Error("Client.Send: message too big: " + message.Count + ". Limit: " + MaxMessageSize);
                return false;
            }
            Log.Warning("Client.Send: not connected!");
            return false;
        }

        // tick: processes up to 'limit' messages
        // => limit parameter to avoid deadlocks / too long freezes if server or
        //    client is too slow to process network load
        // => Mirror & DOTSNET need to have a process limit anyway.
        //    might as well do it here and make life easier.
        // => returns amount of remaining messages to process, so the caller
        //    can call tick again as many times as needed (or up to a limit)
        //
        // Tick() may process multiple messages, but Mirror needs a way to stop
        // processing immediately if a scene change messages arrives. Mirror
        // can't process any other messages during a scene change.
        // (could be useful for others too)
        // => make sure to allocate the lambda only once in transports
        public int Tick(int processLimit, Func<bool> checkEnabled = null)
        {
            // only if pipes were created yet (after connect())
            if (receivePipe == null || sendPipe == null)
                return 0;

            // always process connect FIRST before anything else
            if (processLimit > 0)
            {
                if (receivePipe.CheckConnected())
                {
                    OnConnected?.Invoke();
                    // it counts as a processed message
                    --processLimit;
                }
            }

            // process up to 'processLimit' messages
            for (int i = 0; i < processLimit; ++i)
            {
                // check enabled in case a Mirror scene message arrived
                if (checkEnabled != null && !checkEnabled())
                    break;

                // peek first. allows us to process the first queued entry while
                // still keeping the pooled byte[] alive by not removing anything.
                if (receivePipe.TryPeek(out ArraySegment<byte> message))
                {
                    OnData?.Invoke(message);

                    // IMPORTANT: now dequeue and return it to pool AFTER we are
                    //            done processing the event.
                    receivePipe.TryDequeue();
                }
                // no more messages. stop the loop.
                else break;
            }

            // always process disconnect AFTER anything else
            // (should never process data messages after disconnect message)
            if (processLimit > 0)
            {
                if (receivePipe.CheckDisconnected())
                {
                    OnDisconnected?.Invoke();
                }
            }

            // return what's left to process for next time
            return receivePipe.Count;
        }
    }
}
