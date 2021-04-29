﻿using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Routing.TypeBased;
using Rebus.Tests.Contracts;
using Rebus.Tests.Contracts.Utilities;

namespace Rebus.Tests.Routing
{
    [TestFixture]
    public class TestTypeBasedRouter : FixtureBase
    {
        TypeBasedRouter _router;
        ListLoggerFactory _loggerFactory;

        protected override void SetUp()
        {
            _loggerFactory = new ListLoggerFactory(detailed: true, outputToConsole: true);
            _router = new TypeBasedRouter(_loggerFactory);
        }

        [Test]
        public void ThrowsByDefaultWhenRoutingUnmappedTopic()
        {
            var aggregateException = Assert.Throws<AggregateException>(() =>
            {
                _router.GetDestinationAddress(new Message(NoHeaders, "STRING BODY")).Wait();
            });

            var baseException = aggregateException.GetBaseException();

            Console.WriteLine(baseException);

            Assert.That(baseException, Is.TypeOf<ArgumentException>());
        }

        [Test]
        public void CanGetRouteWhenTypeHasBeenMapped()
        {
            _router.Map<string>("some_endpoint");

            var address = GetDestinationForBody("STRING BODY");

            Assert.That(address, Is.EqualTo("some_endpoint"));
        }

        [Test]
        public void LogsWarningWhenRouteIsOverwritten()
        {
            _router.Map<string>("some_endpoint");
            _loggerFactory.Clear();

            _router.Map<string>("another_endpoint");

            var logLines = _loggerFactory
                .Where(l => l.Level == LogLevel.Warn)
                .ToList();

            Assert.That(logLines.Count, Is.EqualTo(1));
        }

        [Test]
        public void WorksWithMultipleRoutes()
        {
            _router
                .Map<string>("StringDestination")
                .Map<DateTime>("DateTimeDestination")
                .Map<int>("IntDestination");

            Assert.That(GetDestinationForBody("STRING BODY"), Is.EqualTo("StringDestination"));
            Assert.That(GetDestinationForBody(DateTime.Now), Is.EqualTo("DateTimeDestination"));
            Assert.That(GetDestinationForBody(78), Is.EqualTo("IntDestination"));
        }

        [Test]
        public void WorksWithAssemblyRouteMapping()
        {
            _router.MapAssemblyOf<TestNamespaceRouting.AssemblyMessageOne>("AssemblyDestination");

            // All these should be mapped from the entire assembly (along with a ton more, but that's beside the point for this test)
            Assert.That(GetDestinationForBody(new TestNamespaceRouting.AssemblyMessageOne()), Is.EqualTo("AssemblyDestination"));
            Assert.That(GetDestinationForBody(new TestNamespaceRouting.AssemblyMessageTwo()), Is.EqualTo("AssemblyDestination"));
            Assert.That(GetDestinationForBody(new TestNamespaceRouting.SubNamespace.AssemblyMessageSubNamespace()), Is.EqualTo("AssemblyDestination"));
            Assert.That(GetDestinationForBody(new OtherNamespaceRouting.AssemblyMessageOtherNamespace()), Is.EqualTo("AssemblyDestination"));
        }

        [Test]
        public void WorksWithAssemblyRouteMappingAndDerivedClasses()
        {
            _router.MapAssemblyDerivedFrom<TestNamespaceRouting.AssemblyMessageBaseClass>("AssemblyDestination");

            // All these should be mapped from the entire assembly (along with a ton more, but that's beside the point for this test)
            Assert.That(GetDestinationForBody(new TestNamespaceRouting.AssemblyMessageOne()), Is.EqualTo("AssemblyDestination"));
            Assert.That(GetDestinationForBody(new TestNamespaceRouting.SubNamespace.AssemblyMessageSubNamespace()), Is.EqualTo("AssemblyDestination"));

            // These ones should NOT be mapped
            Assert.Throws<AggregateException>(() =>
            {
                GetDestinationForBody(new TestNamespaceRouting.AssemblyMessageTwo());
            });
            Assert.Throws<AggregateException>(() =>
            {
                GetDestinationForBody(new OtherNamespaceRouting.AssemblyMessageOtherNamespace());
            });
        }

        [Test]
        public void WorksWithNamespaceRouteMapping()
        {
            _router.MapAssemblyNamespaceOf<TestNamespaceRouting.AssemblyMessageOne>("AssemblyDestination");

            // All these should be mapped from the namespace and sub-namespace
            Assert.That(GetDestinationForBody(new TestNamespaceRouting.AssemblyMessageOne()), Is.EqualTo("AssemblyDestination"));
            Assert.That(GetDestinationForBody(new TestNamespaceRouting.AssemblyMessageTwo()), Is.EqualTo("AssemblyDestination"));
            Assert.That(GetDestinationForBody(new TestNamespaceRouting.SubNamespace.AssemblyMessageSubNamespace()), Is.EqualTo("AssemblyDestination"));

            // This one should NOT be mapped
            Assert.Throws<AggregateException>(() =>
            {
                GetDestinationForBody(new OtherNamespaceRouting.AssemblyMessageOtherNamespace());
            });
        }

        [Test]
        public void SupportsFallback()
        {
            _router
                .Map<string>("StringEndpoint")
                .Map<DateTime>("DateTimeEndpoint")
                .MapFallback("fallback");

            Assert.That(GetDestinationForBody("hej"), Is.EqualTo("StringEndpoint"));
            Assert.That(GetDestinationForBody(87843784), Is.EqualTo("fallback"));
        }

        [Test]
        public void LogsWarningWhenFallbackIsOverwritten()
        {
            _router.MapFallback("something");
            _loggerFactory.Clear();

            _router.MapFallback("something_else");

            var logLines = _loggerFactory
                .Where(l => l.Level == LogLevel.Warn)
                .ToList();

            Assert.That(logLines.Count, Is.EqualTo(1));
        }

        string GetDestinationForBody(object messageBody)
        {
            return _router.GetDestinationAddress(new Message(NoHeaders, messageBody)).Result;
        }

        static Dictionary<string, string> NoHeaders => new Dictionary<string, string>();
    }
}

// Set up some types we can use to test namespace mapping
namespace Rebus.Tests.Routing.TestNamespaceRouting
{
    class AssemblyMessageBaseClass
    {
    }

    class AssemblyMessageOne : AssemblyMessageBaseClass
    {
    }

    class AssemblyMessageTwo
    {
    }

    namespace SubNamespace
    {
        class AssemblyMessageSubNamespace : AssemblyMessageBaseClass
        {
        }
    }
}

namespace Rebus.Tests.Routing.OtherNamespaceRouting
{
    class AssemblyMessageOtherNamespace
    {
    }
}
