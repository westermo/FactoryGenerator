; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
FG001 | FactoryGenerator | Error | Missing [Inject]ed source for an injected member
FG002 | FactoryGenerator | Error | No usable construction method
FG003 | FactoryGenerator | Error | Cyclic dependency detected
FG004 | FactoryGenerator | Error | Multiple externally provided values of the same type