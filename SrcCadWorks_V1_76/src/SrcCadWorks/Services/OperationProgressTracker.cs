using System;
using SrcCadWorks.Models;

namespace SrcCadWorks.Services
{
    internal sealed class OperationProgressTracker
    {
        private readonly object _sync = new object();
        private OperationProgressSnapshot _snapshot;

        public OperationProgressTracker(string operationName, int totalItems)
        {
            _snapshot = new OperationProgressSnapshot
            {
                OperationName = string.IsNullOrWhiteSpace(operationName) ? "Üretim Paketi" : operationName.Trim(),
                Stage = "HAZIRLIK",
                Index = 0,
                Total = Math.Max(1, totalItems),
                OverallIndex = 0,
                OverallTotal = Math.Max(1, totalItems),
                Elapsed = TimeSpan.Zero
            };
        }

        public void Update(PackageProgress progress)
        {
            if (progress == null) return;
            lock (_sync)
            {
                _snapshot = new OperationProgressSnapshot
                {
                    OperationName = _snapshot.OperationName,
                    Stage = progress.Stage,
                    Index = progress.Index,
                    Total = progress.Total,
                    PartCode = progress.PartCode,
                    OverallIndex = progress.OverallIndex,
                    OverallTotal = progress.OverallTotal,
                    Elapsed = progress.Elapsed,
                    EstimatedRemaining = progress.EstimatedRemaining,
                    IsCompleted = false
                };
            }
        }

        public void Complete(string finalStatus, TimeSpan elapsed)
        {
            lock (_sync)
            {
                _snapshot = new OperationProgressSnapshot
                {
                    OperationName = _snapshot.OperationName,
                    Stage = "TAMAM",
                    Index = _snapshot.Total,
                    Total = _snapshot.Total,
                    PartCode = _snapshot.PartCode,
                    OverallIndex = _snapshot.OverallTotal,
                    OverallTotal = _snapshot.OverallTotal,
                    Elapsed = elapsed,
                    EstimatedRemaining = TimeSpan.Zero,
                    IsCompleted = true,
                    FinalStatus = finalStatus
                };
            }
        }

        public OperationProgressSnapshot Snapshot()
        {
            lock (_sync)
            {
                return new OperationProgressSnapshot
                {
                    OperationName = _snapshot.OperationName,
                    Stage = _snapshot.Stage,
                    Index = _snapshot.Index,
                    Total = _snapshot.Total,
                    PartCode = _snapshot.PartCode,
                    OverallIndex = _snapshot.OverallIndex,
                    OverallTotal = _snapshot.OverallTotal,
                    Elapsed = _snapshot.Elapsed,
                    EstimatedRemaining = _snapshot.EstimatedRemaining,
                    IsCompleted = _snapshot.IsCompleted,
                    FinalStatus = _snapshot.FinalStatus
                };
            }
        }
    }
}
