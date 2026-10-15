using Xunit;

// Lang holds the current language in a static field, and AppTheme holds the current theme the
// same way - deliberately, because both are asked for by bindings dozens of times a frame.
//
// xunit runs separate test classes in parallel by default, so LangTests flipping the language to
// Persian in one thread while ServerRowTests asserts an English label in another produces a
// failure that depends on timing rather than on the code. That is the worst kind of failure to
// leave in a suite: it makes a correct build look wrong, and it makes a wrong build look right
// often enough to be trusted.
//
// The whole run takes under a second, so there is nothing to gain from parallelism here.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
