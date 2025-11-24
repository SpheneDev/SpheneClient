using Sphene.API.Data;
using Sphene.FileCache;
using Sphene.Services.CharaData.Models;

namespace Sphene.Services.CharaData;

public sealed class SpheneCharaFileDataFactory
{
    private readonly FileCacheManager _fileCacheManager;

    public SpheneCharaFileDataFactory(FileCacheManager fileCacheManager)
    {
        _fileCacheManager = fileCacheManager;
    }

    public SpheneCharaFileData Create(string description, CharacterData characterCacheDto)
    {
        return new SpheneCharaFileData(_fileCacheManager, description, characterCacheDto);
    }
}
