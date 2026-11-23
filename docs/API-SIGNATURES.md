# Checking Revit API signatures

`horizun_api_help` reads the assemblies loaded by the selected Revit. It needs no
open document and never invokes a member, constructs an API object, converts a
value or writes to the model. Call `horizun_health` first to identify the host.

Existing calls still use `operation=describe`: the substring filter, inherited
members default (`true`) and `max_members` result cap are unchanged.

```json
{
  "operation": "check_signature",
  "type_name": "Autodesk.Revit.DB.ElementId",
  "member_filter": ".ctor",
  "argument_types": ["System.Int64"],
  "include_inherited": false
}
```

For a method, supply its exact case-sensitive name and the actual CLR argument
types in order. `argument_types` is required, including `[]` for zero arguments;
it accepts at most 32 type names. Use public CLR names from the permitted loaded
Revit/BCL types. Closed generic notation is supported, for example
`System.Collections.Generic.List[Autodesk.Revit.DB.ElementId]`; arrays preserve
their rank (`System.Int32[]`, `System.Int32[,]`). Python `int`, `list`, implicit
null, values, expressions and assembly-qualified names do not resolve as CLR
argument declarations. No assembly is loaded from a caller-provided name.

`check_signature` defaults `include_inherited` to `false`. When explicitly true,
it also includes public inherited static methods and parent interface methods.
Constructors are never inherited. Every overload is examined; `max_members`
does not truncate this operation.

An exact CLR method name also includes special-name methods such as
`Element.get_Parameter` or `op_Implicit`: their signatures are inspected without
executing a getter or operator. Property display names are not method names.

The result includes `revit_year`, `candidate_count`, `matches` with
`exact_match` and `conversions_required`, `ambiguous`, `coverage_complete`,
`unresolved_types`, and `overloads` with rejection reasons and IronPython type
suggestions. Supported implicit compatibility includes numeric widening,
reference assignment, boxing and nullable lifting. Omitted optional parameters
with a metadata default are named explicitly. Multiple compatible signatures
remain ambiguous: the tool does not rank overloads or emulate IronPython's binder.

Unresolved/ambiguous type names, open generic inference, by-ref/pointer signatures,
expanded `params` and potential user-defined implicit operators keep coverage
partial. Potential operators are identified from metadata and never executed.
An empty match list with partial coverage is not proof that no invocation exists.
Compatibility also cannot prove non-null inputs, valid geometry, units, range,
transaction state or valid concrete values.

ElementId hints come from the actual constructors. Revit 2023 has `Int32`;
Revit 2025 has `Int64` and retains the obsolete `Int32` constructor. The hint
names both and marks the obsolete form rather than pretending it disappeared.
For `ICollection[ElementId]`, construct a CLR `List[ElementId]` in IronPython;
a Python list does not establish that declared CLR type.

Python overload errors mention this operation. If a unique explicit Revit CLR
stack frame identifies the type/member, `signature_check` carries that target.
When the traceback also establishes a single CLR argument type, the example is
complete; otherwise `CLR_TYPES` is an explicit placeholder to fill with the
actual types. Untyped receivers, conflicting frames and Python `int`/`list`
never become guessed CLR argument types.

The exact input schema is served by
`horizun://contract/tools/horizun_api_help`. Portable rules/contract tests run
with the existing Core and Server suites; genuine 2023/2025 metadata tests have
their own explicit project in `tests/Horizun.ApiSignature.Tests`.
