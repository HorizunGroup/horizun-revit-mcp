using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    // All resolution attempts must refer to the same document returned by the open.
    public sealed class OpenRecoveryBinding<T> where T : class
    {
        private readonly List<T> _documents = new List<T>();
        public void Record(T document) { _documents.Add(document); }
        // Bind before a resolution is requested; never touch a second document.
        public bool TryBind(T document)
        {
            if (document == null) return false;
            if (_documents.Count == 0) { _documents.Add(document); return true; }
            return Matches(document);
        }
        public bool Matches(T document)
        {
            if (document == null || _documents.Count == 0) return false;
            try { return _documents.All(d => d != null && d.Equals(document)); }
            catch { return false; }
        }
    }

    public interface IOpenRecoveryFailure
    {
        string Message { get; }
        IReadOnlyList<long> ElementIds { get; }
        string DefaultResolution { get; }
        bool Permitted { get; }
        void Resolve();
    }

    public sealed class OpenRecoveryRecord
    {
        public string Message { get; internal set; }
        public IReadOnlyList<long> ElementIds { get; internal set; }
        public string Resolution { get; internal set; }
        // ResolveFailure only requests a resolution; it does not prove the transaction committed.
        public string EffectStatus => "unverified";
    }

    // Absence is an observation, not proof of causation or transaction commit.
    public static class OpenRecoveryEffect
    {
        public static string CheckDeletedId(long id, Func<long, bool> exists)
        {
            try { return exists(id) ? "still_present" : "verified_deleted"; }
            catch { return "unverified"; }
        }
        public static string Check(string resolution, IReadOnlyList<long> ids, Func<long, bool> exists)
        {
            if (resolution != "DeleteElements" || ids == null || ids.Count == 0 || exists == null)
                return "unverified";
            try
            {
                foreach (long id in ids) if (exists(id)) return "unverified";
                return "elements_absent";
            }
            catch { return "unverified"; }
        }
    }
    public sealed class OpenRecoveryProcessor
    {
        private readonly List<OpenRecoveryRecord> _resolved = new List<OpenRecoveryRecord>();
        public IReadOnlyList<OpenRecoveryRecord> Resolved => _resolved.AsReadOnly();
        public bool TryResolve(IOpenRecoveryFailure failure)
        {
            if (failure == null) throw new ArgumentNullException(nameof(failure));
            if (!failure.Permitted || string.IsNullOrWhiteSpace(failure.DefaultResolution)) return false;
            var record = new OpenRecoveryRecord { Message = failure.Message, ElementIds = failure.ElementIds.ToArray(), Resolution = failure.DefaultResolution };
            failure.Resolve();
            _resolved.Add(record);
            return true;
        }
    }
}
