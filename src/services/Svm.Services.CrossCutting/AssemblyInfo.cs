using System.Runtime.CompilerServices;

// Component tests can exercise the coordinator without activating a production Command.
[assembly: InternalsVisibleTo("Svm.FrameworkTests")]
