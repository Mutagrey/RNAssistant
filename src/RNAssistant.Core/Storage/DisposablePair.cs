using System;

namespace RNAssistant.Core.Storage
{
    internal sealed class DisposablePair : IDisposable
    {
        private readonly IDisposable _first;
        private readonly IDisposable _second;

        public DisposablePair(IDisposable first, IDisposable second)
        {
            _first = first;
            _second = second;
        }

        public static IDisposable Acquire(
            Func<IDisposable> acquireFirst,
            Func<IDisposable> acquireSecond)
        {
            if (acquireFirst == null) throw new ArgumentNullException("acquireFirst");
            if (acquireSecond == null) throw new ArgumentNullException("acquireSecond");
            var first = acquireFirst();
            try
            {
                return new DisposablePair(first, acquireSecond());
            }
            catch
            {
                if (first != null) first.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            try
            {
                if (_second != null) _second.Dispose();
            }
            finally
            {
                if (_first != null) _first.Dispose();
            }
        }
    }
}
