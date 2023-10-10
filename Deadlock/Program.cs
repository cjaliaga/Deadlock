using System;
using System.Threading;
using System.Threading.Tasks;
using DryIoc;
using DryIoc.Microsoft.DependencyInjection;
using Microsoft.Azure.WebJobs.Script.WebHost.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Deadlock
{
    class Program
    {
        [ThreadStatic]
        public static int ThreadId;

        static async Task Main(string[] args)
        {
            // Thread 1: Thing1 (transient) -> Thing0 (singleton)
            // Thread 2: Thing2 (singleton) -> Thing1 (transient) -> Thing0 (singleton)

            // This reproduces the dead lock between the Lazy<T> and the IServiceProvider. Whenever singleton or scoped
            // services are resolved a lock is taken (either globally for singletons or per scope for scoped).

            // 1. Thread 1 resolves the Thing1 which is a transient service
            // 2. In parallel, Thread 2 resolves Thing2 which is a singleton
            // 3. Thread 1 enters the factory callback for Thing1 and takes the lazy lock
            // 4. Thread 2 takes the singleton lock for the container when it resolves Thing2
            // 5. Thread 2 enters the factory callback for Thing1 and waits on the lazy lock
            // 6. Thread 1 calls GetRequiredService<Thing0> on the service provider, this waits for the singleton lock that Thread1 has

            // Thread 1 has the lazy lock and is waiting on the singleton lock
            // Thread 2 has the singleton lock an is waiting on the lazy

            var services = new ServiceCollection();

            IServiceProvider sp = null;

            var lazy = new Lazy<Thing1>(() =>
            {
                // Tries to take the singleton lock (global)
                var thing0 = sp.GetRequiredService<Thing0>();
                return new Thing1(thing0);
            });

            services.AddSingleton<Thing0>();
            services.AddTransient(sp =>
            {
                if (ThreadId == 1)
                {
                    Thread.Sleep(3000);
                }
                else
                {
                    // Let Thread 1 over take Thread 2
                    Thread.Sleep(6000);
                }

                return lazy.Value;
            });
            services.AddSingleton<Thing2>();

            sp = new WebHostServiceProvider(services);

            var t1 = Task.Run(() =>
            {
                ThreadId = 1;
                using var scope1 = sp.CreateScope();
                scope1.ServiceProvider.GetRequiredService<Thing1>();
            });

            var t2 = Task.Run(() =>
            {
                ThreadId = 2;
                using var scope2 = sp.CreateScope();
                scope2.ServiceProvider.GetRequiredService<Thing2>();
            });

            await t1;
            await t2;
        }

        public class Thing2
        {
            public Thing2(Thing1 thing1)
            {

            }
        }

        public class Thing1
        {
            public Thing1(Thing0 thing2)
            {
            }
        }

        public class Thing0
        {
            public Thing0()
            {
            }
        }
    }
}