using NUnit.Framework;
using System;
using System.Data.Common;
using System.Threading;
using Medallion.Threading.SqlServer;
using Medallion.Threading.Data;
using Medallion.Threading.Tests.Data;

namespace Medallion.Threading.Tests.SqlServer
{
    public sealed class SqlDistributedReaderWriterLockTest
    {
        [Test]
        public void TestBadConstructorArguments()
        {
            Assert.Catch<ArgumentNullException>(() => new SqlDistributedReaderWriterLock(null!, ConnectionStringProvider.ConnectionString));
            Assert.Catch<ArgumentNullException>(() => new SqlDistributedReaderWriterLock(null!, ConnectionStringProvider.ConnectionString, exactName: true));
            Assert.Catch<ArgumentNullException>(() => new SqlDistributedReaderWriterLock("a", default(string)!));
            Assert.Catch<ArgumentNullException>(() => new SqlDistributedReaderWriterLock("a", default(DbTransaction)!));
            Assert.Catch<ArgumentNullException>(() => new SqlDistributedReaderWriterLock("a", default(DbConnection)!));
            Assert.Catch<FormatException>(() => new SqlDistributedReaderWriterLock(new string('a', SqlDistributedReaderWriterLock.MaxNameLength + 1), ConnectionStringProvider.ConnectionString, exactName: true));
            Assert.DoesNotThrow(() => new SqlDistributedReaderWriterLock(new string('a', SqlDistributedReaderWriterLock.MaxNameLength), ConnectionStringProvider.ConnectionString, exactName: true));
        }

        [Test]
        public void TestGetSafeLockNameCompat()
        {
            SqlDistributedReaderWriterLock.MaxNameLength.ShouldEqual(SqlDistributedLock.MaxNameLength);

            var cases = new[]
            {
                string.Empty,
                "abc",
                "\\",
                new string('a', SqlDistributedLock.MaxNameLength),
                new string('\\', SqlDistributedLock.MaxNameLength),
                new string('x', SqlDistributedLock.MaxNameLength + 1)
            };

            foreach (var lockName in cases)
            {
                // should be compatible with SqlDistributedLock
                SqlDistributedReaderWriterLock.GetSafeName(lockName).ShouldEqual(SqlDistributedLock.GetSafeName(lockName));
            }
        }

        /// <summary>
        /// Tests the logic where upgrading a connection stops and restarts the keepalive
        /// 
        /// NOTE: This is not an abstract test case because it applies ONLY to the combination of 
        /// <see cref="SqlDistributedReaderWriterLock"/> and <see cref="SqlDistributedLockConnectionStrategy.Azure"/>
        /// </summary>
        [Test]
        [NonParallelizable] // sets static KeepaliveHelper.Interval
        public void TestAzureStrategyProtectsFromIdleSessionKillerAfterFailedUpgrade()
        {
            var originalInterval = KeepaliveHelper.Interval;
            try
            {
                KeepaliveHelper.Interval = TimeSpan.FromSeconds(.1);

                var @lock = new SqlDistributedReaderWriterLock(
                    UniqueSafeLockName(nameof(TestAzureStrategyProtectsFromIdleSessionKillerAfterFailedUpgrade)), 
                    ConnectionStringProvider.ConnectionString, 
                    SqlDistributedLockConnectionStrategy.Azure
                );
                using (new IdleSessionKiller(ConnectionStringProvider.ConnectionString, idleTimeout: TimeSpan.FromSeconds(.25)))
                using (@lock.AcquireReadLock())
                {
                    var handle = @lock.AcquireUpgradeableReadLock();
                    handle.TryUpgradeToWriteLock().ShouldEqual(false);
                    handle.TryUpgradeToWriteLockAsync().Result.ShouldEqual(false);
                    Thread.Sleep(TimeSpan.FromSeconds(1));
                    Assert.DoesNotThrow(() => handle.Dispose());
                }
            }
            finally
            {
                KeepaliveHelper.Interval = originalInterval;
            }
        }

        /// <summary>
        /// Demonstrates that we don't multi-thread the connection despite the <see cref="KeepaliveHelper"/>.
        /// 
        /// This test is similar to <see cref="AzureConnectionStrategyTestCases{TEngineFactory}.ThreadSafetyExercise"/>,
        /// but in this case we additionally test lock upgrading which must pause and restart the <see cref="KeepaliveHelper"/>.
        /// 
        /// NOTE: This is not an abstract test case because it applies ONLY to the combination of 
        /// <see cref="SqlDistributedReaderWriterLock"/> and <see cref="SqlDistributedLockConnectionStrategy.Azure"/>
        /// </summary>
        [Test]
        [NonParallelizable] // sets static KeepaliveHelper.Interval
        public void ThreadSafetyExerciseWithLockUpgrade()
        {
            var originalInterval = KeepaliveHelper.Interval;
            try
            {
                KeepaliveHelper.Interval = TimeSpan.FromMilliseconds(1);

                Assert.DoesNotThrow(() =>
                {
                    var @lock = new SqlDistributedReaderWriterLock(
                        UniqueSafeLockName(nameof(ThreadSafetyExerciseWithLockUpgrade)),
                        ConnectionStringProvider.ConnectionString,
                        SqlDistributedLockConnectionStrategy.Azure
                    );
                    for (var i = 0; i < 30; ++i)
                    {
                        using var handle = @lock.AcquireUpgradeableReadLockAsync().Result;
                        Thread.Sleep(1);
                        handle.UpgradeToWriteLock();
                        Thread.Sleep(1);
                    }
                });
            }
            finally
            {
                KeepaliveHelper.Interval = originalInterval;
            }
        }

        private static string UniqueSafeLockName(string baseName) =>
            SqlDistributedReaderWriterLock.GetSafeName($"{baseName}_{TargetFramework.Current}");
    }
}
