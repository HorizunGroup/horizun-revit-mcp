namespace Horizun.Revit.Core
{
    /// <summary>Linked datums belong to the linked document, not the host view.</summary>
    public static class LinkedDatumRules
    {
        public sealed class Verdict
        {
            public string Reason => "linked_not_controllable";
            public bool Cleaned => false;
            public string Suggestion => "Use the link visibility overrides in this view.";
        }

        public static Verdict Cleanup(string kind) => new Verdict();

        public static string ReferenceRefusal(string field, bool linkOwner, bool linked, bool roundTrip)
        {
            if (linkOwner && (!linked || !roundTrip))
                return field + ": linked datum reference must come from CreateLinkReference on the link instance. Nothing was written.";
            return null;
        }

        public static bool SameReference(string instance, string element, string type, string otherInstance, string otherElement, string otherType)
            => !string.IsNullOrEmpty(instance) && !string.IsNullOrEmpty(element) && !string.IsNullOrEmpty(type)
                && instance == otherInstance && element == otherElement && type == otherType;

    }
}
