﻿using System;
using System.Collections.Generic;
using System.Linq;
using EventStore.Persistence.RavenPersistence.Indexes;
using EventStore.Serialization;
using Raven.Client;
using Raven.Client.Exceptions;
using Raven.Client.Indexes;
using Raven.Database.Data;
using Raven.Database.Json;

namespace EventStore.Persistence.RavenPersistence
{
    public class RavenPersistenceEngine : IPersistStreams
    {
        private readonly IDocumentStore store;
        private readonly ISerialize serializer;
        private bool disposed;

        public RavenPersistenceEngine(IDocumentStore store, ISerialize serializer)
        {
            this.store = store;
            this.serializer = serializer;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing || disposed)
                return;

            disposed = true;
            store.Dispose();
        }

        public virtual void Initialize()
        {
            IndexCreation.CreateIndexes(GetType().Assembly, store);
        }

        public virtual IEnumerable<Commit> GetFrom(Guid streamId, int minRevision, int maxRevision)
        {
            return QueryCommits<RavenCommitByRevisionRange>(x => x.StreamId == streamId
                    && x.StreamRevision >= minRevision
                    && x.StartingStreamRevision <= maxRevision);
        }

        public virtual IEnumerable<Commit> GetFrom(DateTime start)
        {
            return QueryCommits<RavenCommitByDate>(x => x.CommitStamp >= start);
        }

        public virtual void Commit(Commit attempt)
        {
            try
            {
                using (var session = store.OpenSession())
                {
                    session.Advanced.UseOptimisticConcurrency = true;
                    session.Store(attempt.ToRavenCommit(serializer));

                    SaveStreamHead(session, attempt.ToRavenStreamHead());

                    session.SaveChanges();
                }
            }
            catch (NonUniqueObjectException e)
            {
                throw new DuplicateCommitException(e.Message, e);
            }
            catch (Raven.Http.Exceptions.ConcurrencyException)
            {
                var savedCommit = LoadSavedCommit(attempt);

                if (savedCommit.CommitId == attempt.CommitId)
                    throw new DuplicateCommitException();

                throw new ConcurrencyException();
            }
            catch (Exception e)
            {
                throw new StorageException(e.Message, e);
            }
        }

        public virtual IEnumerable<Commit> GetUndispatchedCommits()
        {
            return QueryCommits<RavenCommitsByDispatched>(c => c.Dispatched == false);
        }

        public virtual void MarkCommitAsDispatched(Commit commit)
        {
            if (commit == null) throw new ArgumentNullException("commit");

            var patch = new PatchRequest { Type = PatchCommandType.Set, Name = "Dispatched", Value = true };
            var data = new PatchCommandData { Key = commit.ToRavenCommitId(), Patches = new[] { patch } };

            try
            {
                using (var session = store.OpenSession())
                {
                    session.Advanced.DatabaseCommands.Batch(new[] { data });
                    session.SaveChanges();
                }
            }
            catch (Exception e)
            {
                throw new StorageException(e.Message, e);
            }
        }

        public virtual IEnumerable<StreamHead> GetStreamsToSnapshot(int maxThreshold)
        {
            return Query<RavenStreamHead, RavenStreamHeadByHeadRevisionAndSnapshotRevision>(s => s.HeadRevision >= s.SnapshotRevision + maxThreshold).
                Select(s => s.ToStreamHead());
        }

        public virtual Snapshot GetSnapshot(Guid streamId, int maxRevision)
        {
            return Query<RavenSnapshot, RavenSnapshotByStreamIdAndRevision>(x =>
                x.StreamId == streamId && x.StreamRevision <= maxRevision)
                .OrderByDescending(x => x.StreamRevision)
                .FirstOrDefault()
                .ToSnapshot(serializer);
        }

        public virtual bool AddSnapshot(Snapshot snapshot)
        {
            if (snapshot == null)
                return false;

            try
            {
                using (var session = store.OpenSession())
                {
                    var ravenSnapshot = snapshot.ToRavenSnapshot(serializer);
                    session.Store(ravenSnapshot);

                    SaveStreamHead(session, snapshot.ToRavenStreamHead());

                    session.SaveChanges();
                }

                return true;
            }
            catch (Raven.Http.Exceptions.ConcurrencyException)
            {
                return false;
            }
            catch (Exception e)
            {
                throw new StorageException(e.Message, e);
            }
        }

        RavenCommit LoadSavedCommit(Commit attempt)
        {
            try
            {
                using (var session = store.OpenSession())
                {
                    return session.Load<RavenCommit>(attempt.ToRavenCommitId());
                }
            }
            catch (Exception e)
            {
                throw new StorageException(e.Message, e);
            }
        }

        IEnumerable<Commit> QueryCommits<TIndex>(Func<RavenCommit, bool> query)
            where TIndex : AbstractIndexCreationTask, new()
        {
            return Query<RavenCommit, TIndex>(query)
                .Select(x => x.ToCommit(serializer));
        }

        IEnumerable<T> Query<T, TIndex>(Func<T, bool> query)
            where TIndex : AbstractIndexCreationTask, new()
        {
            try
            {
                using (var session = store.OpenSession())
                {
                    return session.Query<T, TIndex>()
                        .Customize(x => x.WaitForNonStaleResults())
                        .Where(query);
                }
            }
            catch (Exception e)
            {
                throw new StorageException(e.Message, e);
            }
        }

        void SaveStreamHead(IDocumentSession session, RavenStreamHead streamHead)
        {
            var head = session.Load<RavenStreamHead>(streamHead.Id);

            if (head == null)
            {
                head = streamHead;
                session.Store(head);
            }
            else
            {
                head.HeadRevision = streamHead.HeadRevision;
                head.SnapshotRevision = streamHead.SnapshotRevision;
            }
        }
    }
}