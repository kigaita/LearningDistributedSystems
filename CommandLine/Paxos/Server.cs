﻿using Akka.Event;
using CommandLine.KVStore;
using Core;

namespace CommandLine.Paxos;

internal class Server : Node
{
    private readonly IServiceProvider sp;
    private readonly string address;
    private readonly string[] peers;

    // Server can be leader, replica, acceptor.
    // leader election happens through phase 1 messages. 
    // leaders are elected during start and after current leader timeout.
    // acceptors and replicas are always online.
    // accetors reacieve phase 1a and reply with phase 1b , 2a and reply 2b
    private readonly Ballot acceptorBallot;

    private AMOApplication app = new();
    private readonly ILoggingAdapter _log = Logging.GetLogger(Context);

    #region Leader
    private Ballot leaderBallot;
    bool isActive = false;

    #region Scout
    private HashSet<string> waitFor = new();
    private Ballot scoutBallot;
    #endregion
    #endregion
    public Server(IServiceProvider sp, string address, string[] peers)
    {
        this.sp = sp;
        this.address = address;
        this.peers = peers;
        this.acceptorBallot = new Ballot(0, address);
        this.scoutBallot = new Ballot(1, address);
        this.leaderBallot = scoutBallot;
        this.AddTimer(new ScoutTimer(), 200);
        waitFor = new(peers);
    }

    public override void ReceiveTimer(ITimer timer)
    {
        if (timer is ScoutTimer)
        {
            // check if elected.
            // Check if we have a leader. 
            // Check if leader is alive.
            // if leader is not alive and we are not leader send Phase1A again.
            this.AddTimer(timer, 200);
        }
    }

    protected override void HandleRequest(IRequest request, string sender)
    {
        if (request is Phase1A p1a)
        {
            HandlePhase1A(p1a, sender);
        }
        if (request is AMORequest aMORequest)
        {
            HandleAMORequest(aMORequest);
        }
        throw new NotImplementedException();
    }

    protected override void ReceiveResponse(IResult result, string sender)
    {
        if (result is P1BResult p1BResult)
        {
            HandleP1BResult(p1BResult, sender);
        }
    }

    // Scout logic for P1B result
    private void HandleP1BResult(P1BResult p1BResult, string sender)
    {
        if (p1BResult.Ballot.Equals(scoutBallot) && waitFor.Contains(sender))
        {
            waitFor.Remove(sender);
            if (waitFor.Count < peers.Length + 1 / 2.0)
            {
                this.leaderBallot = scoutBallot;
                this.isActive = true;
                // todo: Adopt Pvalues.
            }
        }
        else
        {
            this.isActive = false;
            if (this.scoutBallot.ballotNum < p1BResult.Ballot.ballotNum)
            {
                this.scoutBallot = new Ballot(p1BResult.Ballot.ballotNum + 1, address);
            }
        }
    }

    private void HandlePhase1A(Phase1A message, string sender)
    {
        // reply with phase1b if ballot is bigger than mine.
        if (this.acceptorBallot.ballotNum < message.ballot)
        {
            // respond with phase1b
            this.SendToPeer(sender, new Phase1B());
        }
    }

    #region Replica
    private readonly int window = 10;
    private int slotIn = 1, slotOut = 1;
    protected Dictionary<int, Proposal> proposals = new();
    protected Dictionary<int, Decision> decisions = new();

    protected Queue<AMORequest> requests = new();

    public string LeaderAddress { get; set; }

    public void HandleAMORequest(AMORequest request)
    {
        requests.Enqueue(request);
    }

    public void HandleDecision(Decision decision)
    {
        decisions[decision.slotNumber] = decision;
        while (decisions.ContainsKey(slotOut))
        {
            if (proposals.ContainsKey(slotOut))
            {
                // Proposed result at a certain slot was not chosen.
                if (!proposals[slotOut].Request.Equals(decisions[slotOut].Request))
                {
                    requests.Enqueue(proposals[slotOut].Request);
                    proposals.Remove(slotOut);
                }
            }

            this.Perform(decisions[slotOut]);
        }

        this.Propose();
    }

    private void Propose()
    {
        if (LeaderAddress == null)
        {
            return;
        }

        while (requests.Count > 0 && slotIn < slotOut + window)
        {
            if (!decisions.ContainsKey(slotIn))
            {
                var request = requests.Dequeue();
                this.proposals[slotIn] = new Proposal(request, slotIn);
                this.SendToPeer(LeaderAddress, proposals[slotIn]);
                ++slotIn;
            }
        }
    }

    private void Perform(Decision decision)
    {
        for (int i = 1; i < slotOut; i++)
        {
            if (decisions[i].Request.Equals(decision.Request))
            {
                // Message has been chosen again.
                ++slotOut;
                return;
            }
        }

        var response = this.app.Execute(decision.Request);
        this.SendToPeer(decision.Request.address, response);
        ++slotOut;
    }
    #endregion
}
