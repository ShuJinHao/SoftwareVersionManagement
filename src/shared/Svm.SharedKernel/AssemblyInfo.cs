using System.Runtime.CompilerServices;

// Only the persistence transaction boundary can acknowledge pending domain events.
[assembly: InternalsVisibleTo("Svm.EntityFrameworkCore")]
