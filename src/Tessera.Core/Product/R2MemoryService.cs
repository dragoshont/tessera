using System.Security.Cryptography;
using System.Text;
using Tessera.Core.Kernel;

namespace Tessera.Core.Product;

public sealed record MemoryWhy(AssertionRecord Current, IReadOnlyList<AssertionRecord> History, IReadOnlyList<EvidenceRecord> Evidence);

public sealed class R2MemoryService(IEvidenceRepository evidence, IAssertionRepository assertions)
{
    private static readonly ProducerRef Producer = ProducerRef.Create("tessera.r2.user-memory", "1");

    public async Task<AssertionRecord> RememberAsync(string owner,string subject,string predicate,string value,string sourceMessageId,DateTimeOffset now,CancellationToken token=default)
    {
        var pair=Create(owner,subject,predicate,value,sourceMessageId,now);
        if(await evidence.GetAsync(owner,pair.Evidence.EvidenceId,token).ConfigureAwait(false) is not null)
        {var existing=(await assertions.ListHistoryAsync(owner,pair.Assertion.SubjectKey,pair.Assertion.Predicate,token).ConfigureAwait(false)).SingleOrDefault(item=>item.EvidenceRefs.Contains(pair.Evidence.EvidenceId,StringComparer.Ordinal));return existing??throw new InvalidOperationException("Memory idempotency receipt is incomplete.");}
        await evidence.AddAsync(owner,pair.Evidence,token).ConfigureAwait(false);
        await assertions.SaveBatchAsync(owner,[pair.Assertion],token).ConfigureAwait(false);
        return pair.Assertion;
    }

    public async Task<AssertionRecord> CorrectAsync(string owner,string currentId,string value,string sourceMessageId,DateTimeOffset now,CancellationToken token=default)
    {
        var current=await assertions.GetAsync(owner,currentId,token).ConfigureAwait(false)??throw new KeyNotFoundException("Memory not found.");
        var pair=Create(owner,current.SubjectKey,current.Predicate,value,sourceMessageId,now);
        if(await evidence.GetAsync(owner,pair.Evidence.EvidenceId,token).ConfigureAwait(false) is not null)
        {var existing=(await assertions.ListHistoryAsync(owner,current.SubjectKey,current.Predicate,token).ConfigureAwait(false)).SingleOrDefault(item=>item.EvidenceRefs.Contains(pair.Evidence.EvidenceId,StringComparer.Ordinal));return existing??throw new InvalidOperationException("Memory idempotency receipt is incomplete.");}
        await evidence.AddAsync(owner,pair.Evidence,token).ConfigureAwait(false);
        var corrected=AssertionService.Correct(current,pair.Assertion,now,"Explicit user correction");
        await assertions.ApplyCorrectionAsync(owner,corrected.Superseded,corrected.Current,token).ConfigureAwait(false);
        return corrected.Current;
    }

    public async Task<MemoryWhy> WhyAsync(string owner,string assertionId,CancellationToken token=default)
    {
        var current=await assertions.GetAsync(owner,assertionId,token).ConfigureAwait(false)??throw new KeyNotFoundException("Memory not found.");
        var history=await assertions.ListHistoryAsync(owner,current.SubjectKey,current.Predicate,token).ConfigureAwait(false);
        var records=new List<EvidenceRecord>();
        foreach(var id in history.SelectMany(item=>item.EvidenceRefs).Distinct(StringComparer.Ordinal))
        { var item=await evidence.GetAsync(owner,id,token).ConfigureAwait(false); if(item is not null) records.Add(item); }
        return new(current,history,records.AsReadOnly());
    }

    private static (EvidenceRecord Evidence,AssertionRecord Assertion) Create(string owner,string subject,string predicate,string value,string sourceMessageId,DateTimeOffset now)
    {
        subject=ProductContentValidation.Text(subject,nameof(subject),512);
        predicate=ProductContentValidation.Text(predicate,nameof(predicate),256);
        value=ProductContentValidation.Text(value,nameof(value),4096);
        sourceMessageId=ProductContentValidation.Text(sourceMessageId,nameof(sourceMessageId),256);
        var hash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));var identity=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{owner}\n{sourceMessageId}\n{subject}\n{predicate}\n{hash}")));var evidenceId=$"memory:evidence:{identity}";
        var evidenceRecord=EvidenceRecord.Create(evidenceId,owner,"user_message",sourceMessageId,$"conversation-message:{sourceMessageId}",now,now,"SHA-256",1,hash,RetentionState.Active,SensitivityClass.Confidential,Producer,1,value);
        var assertion=AssertionRecord.Create($"memory:assertion:{identity}",owner,subject,predicate,value,AssertionType.UserAsserted,EpistemicStatus.Current,1m,now,null,now,null,[evidenceId],[],"Explicit Remember command",Producer,1);
        return(evidenceRecord,assertion);
    }
}