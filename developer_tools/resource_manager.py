"""
Resource management system for memory cleanup and leak prevention.
Manages QPixmap caching, temporary files, and graphics item lifecycle.
"""

import os
import tempfile
import shutil
import weakref
import gc
from typing import Dict, List, Optional, Set, Any
from PyQt5.QtGui import QPixmap
from PyQt5.QtCore import QObject
import logging

class ResourceTracker:
    """Tracks resources for cleanup."""
    
    def __init__(self):
        self.temp_files: Set[str] = set()
        self.pixmap_refs: List[weakref.ref] = []
        self.object_refs: List[weakref.ref] = []
        self.max_pixmap_cache = 10
        self.max_temp_files = 50
        
    def register_temp_file(self, file_path: str):
        """Register a temporary file for automatic cleanup."""
        self.temp_files.add(file_path)
        if len(self.temp_files) > self.max_temp_files:
            self.cleanup_old_temp_files()
    
    def register_pixmap(self, pixmap: QPixmap):
        """Register a QPixmap for tracking."""
        ref = weakref.ref(pixmap)
        self.pixmap_refs.append(ref)
        if len(self.pixmap_refs) > self.max_pixmap_cache * 2:
            self.cleanup_pixmap_refs()
    
    def register_object(self, obj: QObject):
        """Register a QObject for tracking."""
        ref = weakref.ref(obj)
        self.object_refs.append(ref)
    
    def cleanup_old_temp_files(self):
        """Clean up old temporary files."""
        files_to_remove: List[str] = []
        for file_path in list(self.temp_files):
            try:
                if not os.path.exists(file_path):
                    self.temp_files.remove(file_path)
                    continue
                if os.path.getmtime(file_path) < (time.time() - 3600):
                    files_to_remove.append(file_path)
            except:
                pass
        for file_path in files_to_remove:
            self.cleanup_file(file_path)
    
    def cleanup_pixmap_refs(self):
        """Clean up dead pixmap references."""
        alive_refs = []
        for ref in self.pixmap_refs:
            if ref() is not None:
                alive_refs.append(ref)
        self.pixmap_refs = alive_refs
    
    def cleanup_file(self, file_path: str):
        """Safely remove a file."""
        try:
            if os.path.exists(file_path):
                os.unlink(file_path)
                if file_path in self.temp_files:
                    self.temp_files.remove(file_path)
                return True
        except Exception as e:
            logging.warning(f"Failed to cleanup file {file_path}: {e}")
        return False
    
    def cleanup_all_temp_files(self):
        """Clean up all temporary files."""
        for file_path in list(self.temp_files):
            self.cleanup_file(file_path)
    
    def force_garbage_collection(self):
        """Force garbage collection and report memory status."""
        gc.collect()
        alive_pixmaps = sum(1 for ref in self.pixmap_refs if ref() is not None)
        alive_objects = sum(1 for ref in self.object_refs if ref() is not None)
        return {
            'temp_files': len(self.temp_files),
            'pixmap_refs': len(self.pixmap_refs),
            'alive_pixmaps': alive_pixmaps,
            'object_refs': len(self.object_refs),
            'alive_objects': alive_objects
        }

class PixmapCache:
    """Intelligent QPixmap caching with size limits."""
    
    def __init__(self, max_cache_size_mb: int = 100):
        self.max_cache_size_mb = max_cache_size_mb
        self.cache: Dict[str, Dict[str, Any]] = {}
        self.access_times: Dict[str, float] = {}
        self.total_size_bytes = 0
        self.hits = 0
        self.misses = 0
    
    def get(self, key: str) -> Optional[QPixmap]:
        """Get a pixmap from cache."""

        import time
        if key in self.cache:
            entry = self.cache[key]
            self.access_times[key] = time.time()
            self.hits += 1
            return entry['pixmap']
        self.misses += 1
        return None
    
    def put(self, key: str, pixmap: QPixmap, estimated_size_kb: int = 0):
        """Add a pixmap to cache."""

        import time
        if estimated_size_kb == 0:
            estimated_size_kb = (pixmap.width() * pixmap.height() * 4) / 1024
        estimated_size_bytes = estimated_size_kb * 1024
        if self.total_size_bytes + estimated_size_bytes > self.max_cache_size_mb * 1024 * 1024:
            self._evict_oldest()
        self.cache[key] = {
            'pixmap': pixmap,
            'size_kb': estimated_size_kb,
            'timestamp': time.time()
        }
        self.access_times[key] = time.time()
        self.total_size_bytes += estimated_size_bytes
    
    def _evict_oldest(self):
        """Evict the least recently used items."""
        if not self.cache:
            return
        sorted_keys = sorted(self.access_times.keys(), key=lambda k: self.access_times[k])
        target_size = self.max_cache_size_mb * 1024 * 1024 * 0.8
        while self.total_size_bytes > target_size and sorted_keys:
            key_to_remove = sorted_keys.pop(0)
            if key_to_remove in self.cache:
                removed_size = self.cache[key_to_remove]['size_kb'] * 1024
                del self.cache[key_to_remove]
                if key_to_remove in self.access_times:
                    del self.access_times[key_to_remove]
                self.total_size_bytes -= removed_size
    
    def clear(self):
        """Clear the entire cache."""
        self.cache.clear()
        self.access_times.clear()
        self.total_size_bytes = 0
    
    def get_stats(self) -> Dict[str, Any]:
        """Get cache statistics."""
        return {
            'size_mb': self.total_size_bytes / (1024 * 1024),
            'max_size_mb': self.max_cache_size_mb,
            'items': len(self.cache),
            'hits': self.hits,
            'misses': self.misses,
            'hit_rate': self.hits / (self.hits + self.misses) if (self.hits + self.misses) > 0 else 0
        }

class ResourceManager:
    """Main resource manager coordinating all resource cleanup."""
    
    def __init__(self, logger: Optional[logging.Logger] = None):
        self.logger = logger or logging.getLogger(__name__)
        self.tracker = ResourceTracker()
        self.pixmap_cache = PixmapCache()
        self.cleanup_timer = None
        
    def setup_cleanup_timer(self, interval_ms: int = 30000):
        """Setup periodic cleanup timer."""

        from PyQt5.QtCore import QTimer
        self.cleanup_timer = QTimer()
        self.cleanup_timer.timeout.connect(self.periodic_cleanup)
        self.cleanup_timer.start(interval_ms)
        self.logger.info(f"Setup cleanup timer with {interval_ms}ms interval")
    
    def periodic_cleanup(self):
        """Perform periodic cleanup tasks."""
        try:
            self.tracker.cleanup_old_temp_files()
            self.tracker.cleanup_pixmap_refs()

            import random
            if random.random() < 0.1:
                stats = self.tracker.force_garbage_collection()
                cache_stats = self.pixmap_cache.get_stats()
                self.logger.debug(
                    f"Cleanup stats: {stats['temp_files']} temp files, "
                    f"{stats['alive_pixmaps']}/{stats['pixmap_refs']} pixmaps alive, "
                    f"Cache: {cache_stats['items']} items, "
                    f"{cache_stats['size_mb']:.1f}/{cache_stats['max_size_mb']} MB"
                )
        except Exception as e:
            self.logger.error(f"Error during periodic cleanup: {e}")
    
    def create_temp_file(self, suffix: str = '.tmp', prefix: str = 'fortnite_') -> str:
        """Create a temporary file with automatic cleanup."""

        import tempfile
        temp_fd, temp_path = tempfile.mkstemp(suffix=suffix, prefix=prefix)
        os.close(temp_fd)
        self.tracker.register_temp_file(temp_path)
        return temp_path
    
    def cleanup_snapshot_files(self):
        """Clean up snapshot-related temporary files."""
        temp_dir = tempfile.gettempdir()
        for filename in os.listdir(temp_dir):
            if filename.startswith('fortnite_snapshot_') or filename.startswith('snapshot_'):
                file_path = os.path.join(temp_dir, filename)
                self.tracker.cleanup_file(file_path)
    
    def shutdown(self):
        """Clean shutdown of resource manager."""
        if self.cleanup_timer:
            self.cleanup_timer.stop()
            self.cleanup_timer = None
        self.tracker.cleanup_all_temp_files()
        self.pixmap_cache.clear()
        self.tracker.force_garbage_collection()
        self.logger.info("Resource manager shutdown complete")
_resource_manager_instance = None

def get_resource_manager(logger: Optional[logging.Logger] = None) -> ResourceManager:
    """Get or create the global resource manager instance."""
    global _resource_manager_instance
    if _resource_manager_instance is None:
        _resource_manager_instance = ResourceManager(logger)
    return _resource_manager_instance

import time
