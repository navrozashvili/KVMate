using System.Diagnostics.CodeAnalysis;

// Microsoft.WindowsAppSDK injects UndockedRegFreeWinRT-AutoInitializer.cs into the compilation
// from inside the NuGet package. The file is SDK-owned and cannot be modified. Suppressing CA5392
// on that single method keeps the rule active for every P/Invoke we write ourselves.
[assembly: SuppressMessage(
    "Security",
    "CA5392:Use DefaultDllImportSearchPaths attribute for P/Invokes",
    Scope = "member",
    Target = "M:Microsoft.Windows.Foundation.UndockedRegFreeWinRTCS.NativeMethods.WindowsAppRuntime_EnsureIsLoaded",
    Justification = "Declared in an SDK-injected file we do not own and must not modify.")]

// App has to stay public: the XAML compiler generates its other partial declaration as public.
// Scoped to the one type so CA1515 keeps firing on any other type that becomes public by accident.
[assembly: SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Scope = "type",
    Target = "~T:KVMate.App.App",
    Justification = "Must be public: the XAML compiler generates App's other partial declaration as public.")]
