namespace Lebiru.FileService.Models
{
    internal class ServerSpaceInfo
    {
        private readonly long _maxDiskSpace;
        
        public ServerSpaceInfo(long maxDiskSpaceBytes)
        {
            _maxDiskSpace = maxDiskSpaceBytes;
        }

        public long TotalSpace => _maxDiskSpace;
        public long FreeSpace => Math.Max(0, _maxDiskSpace - UsedSpace);
        public long UsedSpace { get; set; }
    }
}