//-----------------------------------------------------------------------
// <copyright file="IndexQuery.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

using System;
using Sparrow.Json;

namespace Raven.Client.Documents.Queries
{
    /// <summary>
    /// All the information required to query an index
    /// </summary>
    public class IndexQuery : IndexQuery<Parameters>
    {
        /// <summary>
        /// Indicates if query results should be read from cache (if cached previously) or added to cache (if there were no cached items prior)
        /// </summary>
        public bool DisableCaching { get; set; }

        public ulong GetQueryHash(JsonOperationContext ctx)
        {
            using (var hasher = new QueryHashCalculator(ctx))
            {
                hasher.Write(Query);
                hasher.Write(WaitForNonStaleResults);
                hasher.Write(SkipDuplicateChecking);
#if FEATURE_SHOW_TIMINGS
                hasher.Write(ShowTimings);
#endif
                hasher.Write(ExplainScores);
                hasher.Write(WaitForNonStaleResultsTimeout?.Ticks);
                hasher.Write(CutoffEtag);
                hasher.Write(Start);
                hasher.Write(PageSize);
                hasher.Write(QueryParameters);

                return hasher.GetHash();
            }
        }

        public override bool Equals(IndexQuery<Parameters> other)
        {
            if (base.Equals(other) == false)
                return false;

            if (other is IndexQuery iq && DisableCaching.Equals(iq.DisableCaching))
                return true;

            return false;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = base.GetHashCode();
                hashCode = (hashCode * 397) ^ DisableCaching.GetHashCode();
                return hashCode;
            }
        }
    }

    public abstract class IndexQuery<T> : IndexQueryBase<T>, IEquatable<IndexQuery<T>>
    {
        /// <summary>
        /// Allow to skip duplicate checking during queries
        /// </summary>
        public bool SkipDuplicateChecking { get; set; }

        /// <summary>
        /// Whatever a query result should contain an explanation about how docs scored against query
        /// </summary>
        public bool ExplainScores { get; set; }

#if FEATURE_SHOW_TIMINGS
        /// <summary>
        /// Indicates if detailed timings should be calculated for various query parts (Lucene search, loading documents, transforming results). Default: false
        /// </summary>
        public bool ShowTimings { get; set; }
#endif

        /// <summary>
        /// Gets the custom query string variables.
        /// </summary>
        /// <returns></returns>
        protected virtual string GetCustomQueryStringVariables()
        {
            //TODO: Can remove this 
            return string.Empty;
        }

        public virtual bool Equals(IndexQuery<T> other)
        {
            if (ReferenceEquals(null, other))
                return false;
            if (ReferenceEquals(this, other))
                return true;

            return base.Equals(other) &&
#if FEATURE_SHOW_TIMINGS
                   ShowTimings == other.ShowTimings &&
#endif
                   SkipDuplicateChecking == other.SkipDuplicateChecking;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
                return false;
            if (ReferenceEquals(this, obj))
                return true;
            return obj.GetType() == GetType() && Equals((IndexQuery)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = base.GetHashCode();
#if FEATURE_SHOW_TIMINGS
                hashCode = (hashCode * 397) ^ (ShowTimings ? 1 : 0);
#endif
                hashCode = (hashCode * 397) ^ (SkipDuplicateChecking ? 1 : 0);
                return hashCode;
            }
        }

        public static bool operator ==(IndexQuery<T> left, IndexQuery<T> right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(IndexQuery<T> left, IndexQuery<T> right)
        {
            return Equals(left, right) == false;
        }
    }

    public abstract class IndexQueryBase<T> : IIndexQuery, IEquatable<IndexQueryBase<T>>
    {
        private int _pageSize = int.MaxValue;

        /// <summary>
        /// Whether the page size was explicitly set or still at its default value
        /// </summary>
        protected internal bool PageSizeSet { get; private set; }

        /// <summary>
        /// Actual query that will be performed (Lucene syntax).
        /// </summary>
        public string Query { get; set; }

        public T QueryParameters { get; set; }

        /// <summary>
        /// Number of records that should be skipped.
        /// </summary>
        public int Start { get; set; }

        /// <summary>
        /// Maximum number of records that will be retrieved.
        /// </summary>
        public int PageSize
        {
            get => _pageSize;
            set
            {
                _pageSize = value;
                PageSizeSet = true;
            }
        }

        /// <summary>
        /// When set to <c>true</c>> server side will wait until result are non stale or until timeout
        /// </summary>
        public bool WaitForNonStaleResults { get; set; }

        public TimeSpan? WaitForNonStaleResultsTimeout { get; set; }

        /// <summary>
        /// Gets or sets the cutoff etag.
        /// <para>Cutoff etag is used to check if the index has already process a document with the given</para>
        /// <para>etag. Unlike Cutoff, which uses dates and is susceptible to clock synchronization issues between</para>
        /// <para>machines, cutoff etag doesn't rely on both the server and client having a synchronized clock and </para>
        /// <para>can work without it.</para>
        /// <para>However, when used to query map/reduce indexes, it does NOT guarantee that the document that this</para>
        /// <para>etag belong to is actually considered for the results. </para>
        /// <para>What it does it guarantee that the document has been mapped, but not that the mapped values has been reduced. </para>
        /// <para>Since map/reduce queries, by their nature, tend to be far less susceptible to issues with staleness, this is </para>
        /// <para>considered to be an acceptable trade-off.</para>
        /// <para>If you need absolute no staleness with a map/reduce index, you will need to ensure synchronized clocks and </para>
        /// <para>use the Cutoff date option, instead.</para>
        /// </summary>
        public long? CutoffEtag { get; set; }

        public override string ToString()
        {
            return Query;
        }

        public virtual bool Equals(IndexQueryBase<T> other)
        {
            if (ReferenceEquals(null, other))
                return false;
            if (ReferenceEquals(this, other))
                return true;

            return PageSizeSet.Equals(other.PageSizeSet) &&
                   PageSize == other.PageSize &&
                   string.Equals(Query, other.Query) &&
                   Start == other.Start &&
                   WaitForNonStaleResultsTimeout == other.WaitForNonStaleResultsTimeout &&
                   WaitForNonStaleResults.Equals(other.WaitForNonStaleResults) &&
                   Equals(CutoffEtag, other.CutoffEtag);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
                return false;
            if (ReferenceEquals(this, obj))
                return true;
            return obj.GetType() == GetType() && Equals((IndexQuery)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = PageSizeSet.GetHashCode();
                hashCode = (hashCode * 397) ^ PageSize.GetHashCode();
                hashCode = (hashCode * 397) ^ (Query?.GetHashCode() ?? 0);
                hashCode = (hashCode * 397) ^ Start;
                hashCode = (hashCode * 397) ^ (WaitForNonStaleResultsTimeout != null ? WaitForNonStaleResultsTimeout.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ (CutoffEtag != null ? CutoffEtag.GetHashCode() : 0);
                return hashCode;
            }
        }

        public static bool operator ==(IndexQueryBase<T> left, IndexQueryBase<T> right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(IndexQueryBase<T> left, IndexQueryBase<T> right)
        {
            return Equals(left, right) == false;
        }
    }

    public interface IIndexQuery
    {
        int PageSize { set; get; }

        TimeSpan? WaitForNonStaleResultsTimeout { get; }
    }
}
