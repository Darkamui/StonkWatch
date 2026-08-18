using System.Runtime.CompilerServices;

// Lets the test project assert on `internal` diagnostic-only members — currently just
// LiveQuoteCache.SubscriberCount, which exists solely so a test can confirm unsubscribing
// actually removes the subscriber. There is no other externally observable difference
// between "removed" and "leaked": Publish's TryWrite on a leaked, completed channel just
// returns false, so nothing outside the class can see the leak except its size.
[assembly: InternalsVisibleTo("StonkWatch.Web.Tests")]
