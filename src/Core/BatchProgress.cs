namespace SevenZipper.Core;

internal sealed class BatchProgress
{
    private readonly object gate = new();
    private readonly IProgress<ArchiveProgress>? progress;
    private readonly Job[] jobs;
    private double completedFraction;
    private int completedJobs;

    public BatchProgress(int count, IProgress<ArchiveProgress>? progress)
    {
        this.progress = progress;
        jobs = Enumerable.Range(0, count).Select(_ => new Job(this)).ToArray();
        progress?.Report(new("작업 준비", 0, 0, null, OverallPercent: count == 0 ? 100 : 0, TotalJobs: count));
    }

    public Job this[int index] => jobs[index];

    internal sealed class Job(BatchProgress batch) : IProgress<ArchiveProgress>
    {
        private readonly string id = Guid.NewGuid().ToString("N");
        private ArchiveProgress last = new("파일 확인", 0, 0, null);
        private double fraction;
        private bool completed;

        public void Report(ArchiveProgress value)
        {
            lock (batch.gate)
            {
                if (completed) return;
                last = value;
                // Engine completion precedes publishing; reserve the final fraction for a successful commit.
                double next = value.TotalBytes == 0 ? fraction : Math.Max(fraction,
                    Math.Min(0.999, (double)value.CompletedBytes / value.TotalBytes));
                batch.completedFraction += next - fraction;
                fraction = next;
                Publish();
            }
        }

        public void Complete()
        {
            lock (batch.gate)
            {
                if (completed) return;
                completed = true;
                batch.completedFraction += 1 - fraction;
                batch.completedJobs++;
                last = last with { Stage = "완료" };
                Publish();
            }
        }

        private void Publish() => batch.progress?.Report(last with
        {
            JobId = id,
            OverallPercent = batch.completedJobs == batch.jobs.Length ? 100 : 100 * batch.completedFraction / batch.jobs.Length,
            CompletedJobs = batch.completedJobs,
            TotalJobs = batch.jobs.Length
        });
    }
}
