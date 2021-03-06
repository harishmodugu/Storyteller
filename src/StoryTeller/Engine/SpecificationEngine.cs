﻿using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baseline;
using StoryTeller.Model;
using StoryTeller.Remotes;
using StoryTeller.Remotes.Messaging;

namespace StoryTeller.Engine
{
    public interface ISpecificationEngine
    {
        void Enqueue(SpecExecutionRequest request);
        void CancelRunningSpec(string id);
    }

    public class SpecificationEngine : IDisposable, ISpecificationEngine
    {
        private readonly ConsumingQueue _executionQueue;
        private ConsumingQueue _planning;
        private readonly ISpecRunner _runner;
        private readonly ISystem _system;
        private readonly Task _warmup;
        private ConsumingQueue _reader;

        public SpecificationEngine(ISystem system, ISpecRunner runner, IExecutionObserver observer)
        {
            _system = system;
            _runner = runner;

            _executionQueue = new ConsumingQueue(request =>
            {
                if (request.IsCancelled)
                    return;

                _warmup.Wait(30.Seconds());

                observer.SpecStarted(request);
                var results = _runner.Execute(request, _executionQueue);

                if (!request.IsCancelled && (results != null))
                {
                    // TODO -- combine the two things here?
                    request.SpecExecutionFinished(results);
                    observer.SpecFinished(request);
                }
            });

            _warmup = _system.Warmup().ContinueWith(t =>
            {
                if (t.IsFaulted)
                    _runner.MarkAsInvalid(t.Exception);
            });
        }

        public void Dispose()
        {
            _system.Dispose();
            _executionQueue.Dispose();
            _planning?.Dispose();
            _runner.Cancel();
        }

        public void Enqueue(SpecExecutionRequest request)
        {
            _planning.Enqueue(request);
        }

        public void CancelRunningSpec(string id)
        {
            _runner.Cancel(id);
        }

        private SystemRecycled tryToStart()
        {
            CellHandling cellHandling = null;
            try
            {
                cellHandling = _system.Start();
            }
            catch (Exception ex)
            {
                ConsoleWriter.Write(ConsoleColor.Red, ex.ToString());

                var message = new SystemRecycled
                {
                    success = false,
                    fixtures = new FixtureModel[0],
                    system_name = _system.ToString(),
                    system_full_name = _system.GetType().FullName,
                    name = Path.GetFileName(AppContext.BaseDirectory),
                    error = ex.ToString()
                };

                return message;
            }


            var library = FixtureLibrary.CreateForAppDomain(cellHandling);

            startTheConsumingQueues(library);

            return new SystemRecycled
            {
                success = true,
                fixtures = library.Models.GetAll().ToArray(),
                system_name = _system.ToString(),
                name = Path.GetFileName(AppContext.BaseDirectory)
            };
        }

        private void startTheConsumingQueues(FixtureLibrary library)
        {
            _planning = new ConsumingQueue(request =>
            {
                var culture = Project.CurrentProject?.Culture;
                if (culture.IsNotEmpty())
                {
#if NET46
                    Thread.CurrentThread.CurrentCulture = new CultureInfo(culture);
#else
                    CultureInfo.CurrentCulture = new CultureInfo(culture);
#endif
                }

                request.CreatePlan(library);
                _executionQueue.Enqueue(request);
            });

            _planning.Start();
        }

        public void Start(StopConditions stopConditions)
        {
            _runner.UseStopConditions(stopConditions);
            _executionQueue.Start();

            var recycled = tryToStart();
            EventAggregator.SendMessage(recycled);
        }


    }
}