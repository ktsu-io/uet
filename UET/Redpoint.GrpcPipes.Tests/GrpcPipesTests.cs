namespace Redpoint.GrpcPipes.Tests
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using System.Diagnostics;
    using System.Security.Principal;
    using TestPipes;
    using Xunit.Abstractions;
    using static TestPipes.TestService;

    public class GrpcPipesTests
    {
        private readonly ITestOutputHelper _output;

        public GrpcPipesTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private class TestServiceServer : TestServiceBase
        {
            private readonly Action _methodCalled;

            public TestServiceServer(Action methodCalled)
            {
                _methodCalled = methodCalled;
            }

            public override Task<TestResponse> TestMethod(TestRequest request, Grpc.Core.ServerCallContext context)
            {
                _methodCalled();
                return Task.FromResult(new TestResponse());
            }
        }

        [Fact]
        public async Task TestUserPipes()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddXUnit(_output);
            });
            services.AddGrpcPipes();

            var sp = services.BuildServiceProvider();
            var pipeFactory = sp.GetRequiredService<IGrpcPipeFactory>();

            var pipeName = $"test-grpc-pipes-{Process.GetCurrentProcess().Id}";

            var isCalled = false;
            var testService = new TestServiceServer(() => isCalled = true);

            var server = pipeFactory.CreateServer(
                pipeName,
                GrpcPipeNamespace.User,
                testService);
            await server.StartAsync();

            var client = pipeFactory.CreateClient(
                pipeName,
                GrpcPipeNamespace.User,
                channel => new TestServiceClient(channel));

            await client.TestMethodAsync(new TestRequest());

            await server.StopAsync();

            Assert.True(isCalled, "Expected TestMethod to be called");
        }

        private static bool IsAdministrator
        {
            get
            {
                if (OperatingSystem.IsWindows())
                {
                    using (var identity = WindowsIdentity.GetCurrent())
                    {
                        var principal = new WindowsPrincipal(identity);
                        return principal.IsInRole(WindowsBuiltInRole.Administrator);
                    }
                }
                return false;
            }
        }

        [SkippableFact]
        public async Task TestComputerPipes()
        {
            Skip.IfNot(IsAdministrator);

            var services = new ServiceCollection();
            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddXUnit(_output);
            });
            services.AddGrpcPipes();

            var sp = services.BuildServiceProvider();
            var pipeFactory = sp.GetRequiredService<IGrpcPipeFactory>();

            var pipeName = $"test-grpc-pipes-{Process.GetCurrentProcess().Id}";

            var isCalled = false;
            var testService = new TestServiceServer(() => isCalled = true);

            var server = pipeFactory.CreateServer(
                pipeName,
                GrpcPipeNamespace.Computer,
                testService);
            await server.StartAsync();

            var client = pipeFactory.CreateClient(
                pipeName,
                GrpcPipeNamespace.Computer,
                channel => new TestServiceClient(channel));

            await client.TestMethodAsync(new TestRequest());

            await server.StopAsync();

            Assert.True(isCalled, "Expected TestMethod to be called");
        }

        [Fact]
        public async Task TestNewServerCanRemoveOldPipe()
        {
            Skip.IfNot(IsAdministrator);

            var services = new ServiceCollection();
            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddXUnit(_output);
            });
            services.AddGrpcPipes();

            var sp = services.BuildServiceProvider();
            var pipeFactory = sp.GetRequiredService<IGrpcPipeFactory>();

            var pipeName = $"test-grpc-pipes-2-{Process.GetCurrentProcess().Id}";

            var isCalled = false;
            var testService1 = new TestServiceServer(() => { });
            var testService2 = new TestServiceServer(() => isCalled = true);

            var server = pipeFactory.CreateServer(
                pipeName,
                GrpcPipeNamespace.Computer,
                testService1);
            await server.StartAsync();

            var server2 = pipeFactory.CreateServer(
                pipeName,
                GrpcPipeNamespace.Computer,
                testService2);
            await server2.StartAsync();

            var client = pipeFactory.CreateClient(
                pipeName,
                GrpcPipeNamespace.Computer,
                channel => new TestServiceClient(channel));

            await client.TestMethodAsync(new TestRequest());

            await server.StopAsync();

            await server2.StopAsync();

            Assert.True(isCalled, "Expected TestMethod to be called on second server");
        }
    }
}