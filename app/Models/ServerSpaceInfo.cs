namespace Lebiru.FileService.Models
{
    internal class ServerSpaceInfo
    {
        public ServerSpaceInfo()
        {
        }

        public long TotalSpace { get; set; }
        public long FreeSpace { get; set; }
        public long UsedSpace { get; set; }
    }
}