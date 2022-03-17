﻿using Abp.Runtime.Caching;
using Shesha.Authorization.Dtos;

namespace Shesha.Authorization
{
    public static class CacheManagerExtensions
    {
        public static ITypedCache<string, CustomUserPermissionCacheItem> GetCustomUserPermissionCache(this ICacheManager cacheManager)
        {
            return cacheManager.GetCache<string, CustomUserPermissionCacheItem>(CustomUserPermissionCacheItem.CacheStoreName);
        }

        public static ITypedCache<string, RequiredPermissionCacheItem> GetApiPermissionCache(this ICacheManager cacheManager)
        {
            return cacheManager.GetCache<string, RequiredPermissionCacheItem>(RequiredPermissionCacheItem.CacheStoreName);
        }

    }
}
