"""Disposable current-schema fixture for lifecycle and retry browser journeys."""
import hashlib, json, sqlite3, sys, time, uuid
from datetime import datetime, timezone
path, source_id, state = sys.argv[1:4]
con = sqlite3.connect(path, timeout=30)
con.row_factory = sqlite3.Row
con.execute('PRAGMA foreign_keys=ON')
con.execute('PRAGMA busy_timeout=8000')
source = dict(con.execute('SELECT * FROM Sessions WHERE SessionId=?', (source_id,)).fetchone())
snapshot = dict(con.execute('SELECT * FROM SessionSnapshots WHERE SessionId=?', (source_id,)).fetchone())
run_id, session_id, activation_id, input_id, response_id = [str(uuid.uuid4()) for _ in range(5)]
now = int(time.time()*1000)
def utc(ms): return datetime.fromtimestamp(ms/1000, timezone.utc).isoformat()
def insert(table, values):
    keys=list(values)
    con.execute('INSERT INTO '+table+' ('+','.join(keys)+') VALUES ('+','.join('?' for _ in keys)+')', tuple(values.values()))
source.update(SessionId=session_id, Status='Created', Mode='Text', PendingMode=None, Revision=1, RuntimeEpoch=0,
    ArchivedAtUtc=None, DurablyDeletedAtUtc=None, CreatedAtUtc=now, UpdatedAtUtc=now, Title='Retry fixture' if state=='retry' else 'Historical run',
    Surfaces=2, PendingAgentInputIdsJson='[]', OriginJson=json.dumps(dict(kind=3,initialBackgroundAgentRunId=run_id,reportCompletionToOrigin=False,initialSurface=2)))
snapshot.update(SessionId=session_id, LastEntrySequence=1 if state=='retry' else 2, Summary='', SummarizedThroughEntrySequence=0, PendingTopic=None,
    LifecycleStatus='Active', LifecycleReason=None, LifecycleChangedAtUtc=now, DeadlineAtUtc=None, UpdatedAtUtc=now)
insert('Sessions',source); insert('SessionSnapshots',snapshot)
def entry(id, sequence, role, text, response=None):
    insert('ConversationEntries',dict(EntryId=id,SessionId=session_id,EntrySequence=sequence,Role=role,Text=text,ResponseId=response,
        Status='Completed',DeliveryMode='Text',HeardTextEndExclusive=0,ReceivedTextEndExclusive=len(text),CreatedAtUtc=now))
entry(input_id,1,'User','Review observable completed work')
a=dict(activationId=activation_id,sessionId=session_id,kind=5,sourceEntryIds=[input_id],sourceEventId=input_id,
    triggerOccurrenceId=None,sourceSessionId=None,sourceAgentRunId=None,dedupeKey='browser-fixture:'+run_id,admittedAtUtc=utc(now),evidenceJson=None)
p=dict(admission=dict(activation=a,definitionId=source['AgentId'],definitionVersion=source['AgentVersion'],
    pinnedPersona=json.loads(source['PinnedPersonaJson'] or source['DefinitionJson']) if source['PinnedPersonaJson'] else json.loads(source['DefinitionJson'])['identity'],responseId=response_id,outputContract=1),
    pinnedModel=dict(catalogKey='scripted-alpha',providerAlias='primary-llm',modelId='scripted-alpha',reasoningEffort='medium'),attemptCount=1,maxAttempts=3,
    claim=None,cancellationRequested=False,cancellationRequestedAtUtc=None,knownEffectSummary=None,progress=None,checkpoint=None,result=None,failure=None,
    sideEffect=dict(disposition=0,toolCallId=None,actionHash=None,updatedAtUtc=None),approval=None,pinnedSkillCatalog=[],activeSkillKeys=[],skillLoadCount=0,loadedCapabilityIds=[],capabilityLoadCount=0)
if state=='retry':
    p['failure']=dict(code='provider-unavailable',summary='The provider is temporarily unavailable.',failedAtUtc=utc(now),replaySafe=True)
else:
    outcome_id=str(uuid.uuid4()); text='P7G historical work preserved.'
    entry(outcome_id,2,'Assistant',text,response_id)
    p['result']=dict(outcomeKind=0,outcomeEntryId=outcome_id,text=text,completedAtUtc=utc(now),attentionRequired=False)
insert('Activations',dict(ActivationId=activation_id,SessionId=session_id,AgentInstanceId=source['AgentInstanceId'],ProfileId=snapshot['ProfileId'],
    DedupeKey=a['dedupeKey'],BackgroundSourceKey=None,AdmissionHash=hashlib.sha256(run_id.encode()).hexdigest(),PayloadJson=json.dumps(a),AdmittedAtUtc=now))
insert('ActivationSourceEntries',dict(SessionId=session_id,EntryId=input_id,ActivationId=activation_id,Ordinal=0))
insert('AgentRuns',dict(AgentRunId=run_id,ActivationId=activation_id,SessionId=session_id,AgentInstanceId=source['AgentInstanceId'],ProfileId=snapshot['ProfileId'],
    Status=3 if state=='retry' else 4,Revision=2,NextRetryAtUtc=now+86400000 if state=='retry' else None,LeaseExpiresAtUtc=None,ApprovalExpiresAtUtc=None,
    CreatedAtUtc=now,UpdatedAtUtc=now,PayloadJson=json.dumps(p)))
assert not con.execute('PRAGMA foreign_key_check').fetchall()
con.commit();con.close();print(run_id)
