// Keep execution and allocation regressions responsive on shared CI workers.
[assembly: CollectionBehavior(MaxParallelThreads = 6)]
