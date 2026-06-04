## Click image to open video
[![Watch the video](https://img.youtube.com/vi/BmZq7yu3awc/maxresdefault.jpg)](https://www.youtube.com/watch?v=BmZq7yu3awc)

## Infinite Terrain Generator

An infinite terrain system built in Unity using compute shaders and the Marching Cubes algorithm.

### Features

* Generates terrain meshes entirely on the GPU using Marching Cubes.
* Creates terrain density values from a procedural noise-based scalar field computed in a shader.
* Uses a chunk recycling system to avoid costly GameObject creation and destruction.

  * Chunks that leave the player's render range have their mesh data cleared and are returned to a reuse pool.
  * When new terrain enters range, recycled chunks are reassigned and regenerated with new mesh data.
* Maintains a fixed number of active chunks in the world, reducing memory allocations and garbage collection.
* Supports multiple Levels of Detail (LOD), generating lower-resolution meshes for distant terrain to improve performance and increase render distance.
* Uses the Unity Job System with Burst Compiler optimizations to efficiently identify and recycle out-of-range chunks.
* Generates chunks asynchronously through a coroutine system.

  * If the player enters a new chunk, generation is reprioritized so nearby terrain loads first.
