/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Dolittle. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 * --------------------------------------------------------------------------------------------*/


using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dolittle.Lifecycle;
using Dolittle.Events;
using Dolittle.Runtime.Events;
using Dolittle.Runtime.Events.Store;
using Dolittle.Artifacts;
using Dolittle.Execution;

namespace Dolittle.Runtime.Events.Store.InMemory
{
    /// <summary>
    /// Manages the committing and fetching of event streams for the <see cref="EventStore" />
    /// </summary>
    [SingletonPerTenant]
    public class EventStreamCommitterAndFetcher : ICommitEventStreams, IFetchCommittedEvents, IFetchEventSourceVersion
    {
        private readonly object lock_object = new object();

        private readonly List<CommittedEventStream> _commits = new List<CommittedEventStream>();
        private readonly HashSet<CommitId> _duplicates = new HashSet<CommitId>();
        private readonly ConcurrentDictionary<EventSourceKey,VersionedEventSource> _versions = new ConcurrentDictionary<EventSourceKey,VersionedEventSource>();
        private readonly ConcurrentDictionary<EventSourceKey,EventSourceVersion> _currentVersions = new ConcurrentDictionary<EventSourceKey,EventSourceVersion>();

        private ulong _sequenceNumber = 0;

        /// <summary>
        /// Increments the count of commits
        /// </summary>
        /// <returns>The number of commits</returns>
        public ulong IncrementCount()
        {
            lock(lock_object)
            {
                return ++_sequenceNumber;
            }
        }

        /// <inheritdoc />
        public CommittedEventStream Commit(UncommittedEventStream uncommittedEvents)
        {
            return Commit(uncommittedEvents,IncrementCount());
        }

        /// <inheritdoc />
        CommittedEventStream Commit(UncommittedEventStream uncommittedEvents, CommitSequenceNumber commitSequenceNumber)
        {
            lock(lock_object)
            {
                ThrowIfDuplicate(uncommittedEvents.Id);
                ThrowIfConcurrencyConflict(uncommittedEvents.Source);

                var commit = new CommittedEventStream(commitSequenceNumber, uncommittedEvents.Source, uncommittedEvents.Id, uncommittedEvents.CorrelationId, uncommittedEvents.Timestamp, uncommittedEvents.Events);
                _commits.Add(commit);
                _duplicates.Add(commit.Id);
                _versions.AddOrUpdate(commit.Source.Key,commit.Source,(id,ver) => commit.Source);
                return commit;
            }
        }

        void UpdateVersion(CommittedEventStream Commits)
        {
            _currentVersions.AddOrUpdate(Commits.Source.Key, Commits.Source.Version, (key, value) => Commits.Source.Version);
        }

        void ThrowIfDuplicate(CommitId commitId)
        {
            if (!_duplicates.Contains(commitId))
                return;

            throw new CommitIsADuplicate();
        }

        void ThrowIfConcurrencyConflict(VersionedEventSource version)
        {
            VersionedEventSource ver;
            if(_versions.TryGetValue(version.Key, out ver))
            {
                if (ver == version || ver.Version.Commit >= version.Version.Commit)
                {
                    throw new EventSourceConcurrencyConflict();
                }
            }
        }

        /// <inheritdoc />
        public Commits Fetch(EventSourceKey key)
        {
            return new Commits(_commits.Where(c => c.Source.Key == key).ToList());
        }

        /// <inheritdoc />
        public Commits FetchFrom(EventSourceKey key, CommitVersion commitVersion)
        {
            return new Commits(_commits.Where(c => c.Source.Key == key && c.Source.Version.Commit >= commitVersion).ToList());
        }

        /// <inheritdoc />
        public Commits FetchAllCommitsAfter(CommitSequenceNumber commit)
        {
            return new Commits(_commits.Where(c => c.Sequence > commit).ToList());
        }

        /// <inheritdoc />
        public SingleEventTypeEventStream FetchAllEventsOfType(ArtifactId artifactId)
        {
            var commits = _commits.Where(c => c.Events.Any(e => e.Metadata.Artifact.Id == artifactId));
            return GetEventsFromCommits(commits, artifactId);
        }

        /// <inheritdoc />
        public SingleEventTypeEventStream FetchAllEventsOfTypeAfter(ArtifactId artifactId, CommitSequenceNumber commitSequenceNumber)
        {
            var commits = _commits.Where(c => c.Sequence > commitSequenceNumber && c.Events.Any(e => e.Metadata.Artifact.Id == artifactId));
            return GetEventsFromCommits(commits, artifactId);
        }

         SingleEventTypeEventStream GetEventsFromCommits(IEnumerable<CommittedEventStream> commits, ArtifactId eventType)
         {
            var events = new List<CommittedEventEnvelope>();
            foreach(var commit in commits)
            {
                events.AddRange(commit.Events.FilteredByEventType(eventType).Select(e => new CommittedEventEnvelope(commit.Sequence,e.Metadata,e.Event)));
            }
            return new SingleEventTypeEventStream(events);
         }

        /// <inheritdoc />
        public EventSourceVersion GetCurrentVersionFor(EventSourceKey eventSource)
        {
            VersionedEventSource v;
            if(_versions.TryGetValue(eventSource, out v))
            {
                return v.Version;
            }
            return EventSourceVersion.NoVersion;
        }
        /// <inheritdoc />
        public EventSourceVersion GetNextVersionFor(EventSourceKey eventSource)
        {
            return GetCurrentVersionFor(eventSource).NextCommit();
        }
    }
}