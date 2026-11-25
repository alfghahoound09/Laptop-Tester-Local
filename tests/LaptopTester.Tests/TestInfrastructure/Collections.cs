using Xunit;

namespace LaptopTester.Tests.TestInfrastructure;

[CollectionDefinition("Audio", DisableParallelization = true)]
public sealed class AudioCollection;

[CollectionDefinition("Heavy", DisableParallelization = true)]
public sealed class HeavyCollection;
