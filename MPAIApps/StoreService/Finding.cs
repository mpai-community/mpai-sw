namespace Mpai.StoreService;

// WHAT THE STORE FOUND IN AN L3. Validation reports here, and does not refuse.
//   Source    which check: L2 (validation), Package
//   Severity  "error" - the L3 breaks the rule; "warning" - it may be a problem
//   Text      what, in words a person can act on
public sealed record Finding(string Source, string Severity, string Text);
