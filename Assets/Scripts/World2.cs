using UnityEngine;
using System;
using Unity.Burst;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Jobs;
using System.Collections;
using System.Collections.Generic;
//Raghav Dukkipati

//ChunkManager class
//Spawns terrain
//Uses marching cubes algorithm to generate vertex data for chunk meshes
//Manages chunks
//disables, enables, recycles
//Use of compute shader and marching cubes inspired by Sebastian Lague
public class World2 : MonoBehaviour
{
    //Coroutines, multithreaded jobs, compute shader and marching cubes algorithm for mesh generation

    //Seed that is provided to perlin noise
    public int seed = 1337;

    //player that world is centered around
    public Transform player;

    //material applied to terrain
    public Material material;

    //Compute Shader for running Marching Cubes algorithm in parallel on the gpu
    public ComputeShader shader;

    //Used for sending product of marching cubes algorithm back to cpu
    //Holds all vertex and color data of the chunk in a Triangle struct
    ComputeBuffer triangleBuffer;

    //Single element buffer used as an index counter
    //Also gives the amount of triangles added to the buffer
    ComputeBuffer triCountBuffer;


    //Holds all chunks created
    //Allows for easy destruction of chunks at the end
    //And maybe some other benefits I don't know
    GameObject chunkHolder;

    //Dictionary for keeping track of existing chunks
    //When I need to disable a chunk, I can grab it from this dictionary
    Dictionary<ChunkKey, Chunk> chunkDictionary;

    //Recycles chunk game objects to prevent wasteful creation and destruction
    //Means there's a constant pool of chunks being recycled
    //The only time chunks are created are at the very beginning
    //The only time chunks are destroyed are at application close
    Queue<Chunk> recyclePool;

    //Array of all chunks that existed last frame
    //Iterate in unity job to find chunks that need to be disabled
    NativeArray<ChunkKey> oldChunks;


    //Array of all chunks that exist this frame
    //This becomes oldChunks at the end of each update
    NativeArray<ChunkKey> newChunks;

    //precalculated array of chunk positions relative to world origin
    //At runtime, every frame, add player position to each chunk position in chunkInfos
    //Gives chunks that should exist this frame
    NativeArray<ChunkKey> chunkInfos;

    //Array of chunks that need to be enabled
    //Iterated through in a coroutine to prevent stalling main thread
    //Coroutine is cancelled if player moves into a new chunk
    //Array instead of queue so order of chunks is maintained even with multithreaded job
    NativeArray<ChunkKey> chunksToEnable;

    //Queue of chunks to disable
    //Entire queue is emptied immediately unlike chunksToEnable
    //This is so there aren't new game objects constantly being created if player is moving super fast
    NativeQueue<ChunkKey> chunksToDisable;


    //Basically just chunkInfos, but a hash set
    //I use this for distance checking
    //Take current chunk, subtract playerChunkPosition
    //playerChunkPosition = playerWorldPosition / (chunkSize << LOD)
    //If result exists in the hash set, it doesn't need to be disabled
    NativeParallelHashSet<ChunkKey> positionHashSet;

    //Basically just chunkDictionary minus the chunk values
    //I use this because I can send it into a unity job
    //All chunks that currently exist in the world
    NativeParallelHashSet<ChunkKey> existingChunks;

    //Gives size of chunkInfos, oldChunks, newChunks
    //max amount of chunks that will exist in the world
    int totalChunkCount;

    //Size of each chunk in world units along each axis
    public int chunkSize = 32;

    //How large each step is in world units when sampling points in marching cubes
    public int chunkStep = 2;

    public int batchSize = 50;

    //How many level of detail layers there are
    //Minimum value is 1 layer which is lod 0
    int lodLevels = 12;

    //4 would mean an 8x8x8 around the player for each lod level
    public int viewDistance = 4;

    //isoLevel for marching cubes
    public static float isoLevel = 0;


    //How many thread groups along each axis for the compute shader
    int threadGroup;

    //Max amount of triangles per chunk
    //max of 5 triangles per voxel
    int maxTriangleCount;


    float3 playerCurrentPosition;
    int3 playerChunkPosition;
    int3 playerOldChunkPosition;

    void Awake()
    {
        //See line 279
        InitializeComputeShaderVariables();
        //See line 291
        CreateBuffers();
        //See line 297
        AllocateAndFillChunkStructures();

    }




    //Spawn entire world immediately
    //Different than update where I use a coroutine
    //Here I stall main thread until whole world is spawned
    void Start()
    {

    }


    //I have a dedicated coroutine variable so I can
    //stop it when necessary
    Coroutine EnableCoroutine;

    //Allows me to do some unique logic on the first update
    bool firstUpdate = true;

    void Update()
    {
        //Grab player position
        playerCurrentPosition = new float3(player.position.x, player.position.y, player.position.z);

        //Convert player position to chunk coordinates
        playerChunkPosition = CalculateChunkPosition(playerCurrentPosition, chunkSize, 0);

        //Don't update if player hasn't moved chunks
        if (!firstUpdate && math.all(playerChunkPosition == playerOldChunkPosition))
        {
            return;
        }

        //If player has moved, cancel the running coroutine
        if (EnableCoroutine != null)
        {
            StopCoroutine(EnableCoroutine);
            EnableCoroutine = null;
        }


        //Queue chunks to be enabled and disabled
        //See line 329
        DisableEnableJob job = new DisableEnableJob
        {
            chunkInfos = chunkInfos,
            oldChunks = oldChunks,
            positionHashSet = positionHashSet,
            existingChunks = existingChunks,
            newChunks = newChunks,
            chunksToEnable = chunksToEnable,
            chunksToDisable = chunksToDisable.AsParallelWriter(),
            playerCurrentPosition = playerCurrentPosition,
            firstUpdate = firstUpdate,
            chunkSize = chunkSize,
        };

        job.Schedule(chunkInfos.Length, 32).Complete();

        //Disable chunks immediately
        while (chunksToDisable.Count > 0)
        {
            ChunkKey chunkKey = chunksToDisable.Dequeue();
            //See line 415
            DisableChunk(chunkKey);
        }

        //Enable chunks in coroutine
        //See line 386
        EnableCoroutine = StartCoroutine(EnableChunks(chunksToEnable, batchSize));

        //swap oldChunks and newChunks
        var temp = oldChunks;
        oldChunks = newChunks;
        newChunks = temp;

        playerOldChunkPosition = playerChunkPosition;

        firstUpdate = false;


    }


    //=========================================================
    //===============INITIALIZER FUNCTIONS=====================             
    //=========================================================
    //Some variables for the compute shader that have to be calculated at runtime 
    void InitializeComputeShaderVariables()
    {
        //Compute Shader variables
        int numVoxels = chunkSize / chunkStep;
        maxTriangleCount = 5 * (numVoxels * numVoxels * numVoxels);
        threadGroup = numVoxels / 8;

    }

    //Create buffers to be sent between cpu and gpu
    //Have a set size so set to maximum size possible given amount of voxels per chunk
    //We need triCountBuffer to tell us how many triangles were actually added
    void CreateBuffers()
    {
        triangleBuffer = new ComputeBuffer(maxTriangleCount, sizeof(float) * 10 * 3, ComputeBufferType.Structured);
        triCountBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw);
    }

    void AllocateAndFillChunkStructures()
    {
        int outer = viewDistance * viewDistance * viewDistance * 8;
        int inner = (viewDistance - 2) * (viewDistance - 2) * (viewDistance - 2);
        int totalChunkCount = outer + ((outer - inner) * (lodLevels - 1));

        chunkInfos = new NativeArray<ChunkKey>(totalChunkCount, Allocator.Persistent);
        newChunks = new NativeArray<ChunkKey>(totalChunkCount, Allocator.Persistent);
        oldChunks = new NativeArray<ChunkKey>(totalChunkCount, Allocator.Persistent);
        chunksToEnable = new NativeArray<ChunkKey>(totalChunkCount, Allocator.Persistent);

        //Hash sets
        positionHashSet = new NativeParallelHashSet<ChunkKey>(totalChunkCount, Allocator.Persistent);
        existingChunks = new NativeParallelHashSet<ChunkKey>(totalChunkCount, Allocator.Persistent);

        //Queues
        chunksToDisable = new NativeQueue<ChunkKey>(Allocator.Persistent);

        FillChunkInfos(positionHashSet, chunkInfos);

        //Chunk trackers
        chunkHolder = new GameObject("ChunkHolder");
        chunkDictionary = new Dictionary<ChunkKey, Chunk>();
        recyclePool = new Queue<Chunk>();
    }


    //=========================================================
    //===============MULTITHREADED JOB=========================          
    //=========================================================
    //Unity job that queues chunks to be disabled and enabled
    //Note, I don't do distance checks when checking chunks to be disabled
    //Instead, I check if that chunk - playerChunkPosition exists in a hashSet of
    //chunk postions centered around the origin
    [BurstCompile]
    public struct DisableEnableJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<ChunkKey> chunkInfos;
        [ReadOnly] public NativeArray<ChunkKey> oldChunks;
        [ReadOnly] public NativeParallelHashSet<ChunkKey> positionHashSet;
        [ReadOnly] public NativeParallelHashSet<ChunkKey> existingChunks;
        [WriteOnly] public NativeArray<ChunkKey> newChunks;
        public NativeQueue<ChunkKey>.ParallelWriter chunksToDisable;
        public NativeArray<ChunkKey> chunksToEnable;
        public float3 playerCurrentPosition;
        public bool firstUpdate;
        public int chunkSize;

        public void Execute(int index)
        {
            int3 playerChunkPosition;

            //Disabling
            if (!firstUpdate)
            {
                //Grab chunks from oldChunks
                ChunkKey oldChunkKey = oldChunks[index];
                playerChunkPosition = CalculateChunkPosition(playerCurrentPosition, chunkSize, oldChunkKey.lod);
                //Subtract playerChunkPosition from oldChunkKey and then check if it exists in
                //positionHashSet/chunkInfos
                ChunkKey checkKey = new ChunkKey(oldChunkKey.position - playerChunkPosition, oldChunkKey.lod);
                if (!positionHashSet.Contains(checkKey))
                {
                    //If it doesn't exist, then it is not a valid chunk
                    chunksToDisable.Enqueue(oldChunkKey);
                }
            }

            //Enabling
            ChunkKey newChunkKey = chunkInfos[index];
            playerChunkPosition = CalculateChunkPosition(playerCurrentPosition, chunkSize, newChunkKey.lod);
            newChunkKey.position += playerChunkPosition;
            newChunks[index] = newChunkKey;
            if (!existingChunks.Contains(newChunkKey))
            {
                chunksToEnable[index] = newChunkKey;
            }
            else
            {
                //Because I'm using an array for enabling chunks to maintain order
                //I have to set chunks that shouldn't be enabled to a "null" value
                chunksToEnable[index] = new ChunkKey(int3.zero, -1);
            }


        }
    }

    //=========================================================
    //===============COROUTINE=================================               
    //=========================================================
    //Coroutine for creating and disabling chunks over multiple frames
    IEnumerator EnableChunks(NativeArray<ChunkKey> chunksToEnable, int batchSize)
    {

        int count = 0;
        for (int i = 0; i < chunksToEnable.Length; i++)
        {
            ChunkKey chunkKey = chunksToEnable[i];
            if (chunkKey.lod == -1)
            {
                continue;
            }
            RenderChunk(chunkKey);
            count++;

            if (count == batchSize)
            {
                count = 0;
                yield return null;
            }
        }

        yield return null;

    }




    //Disables chunks
    void DisableChunk(ChunkKey chunkKey)
    {
        if (chunkDictionary.TryGetValue(chunkKey, out Chunk chunk))
        {
            chunk.mesh.Clear();
            recyclePool.Enqueue(chunk);
            chunkDictionary.Remove(chunkKey);
            existingChunks.Remove(chunkKey);
        }
    }

    //Renders chunks
    void RenderChunk(ChunkKey chunkKey)
    {
        Chunk chunk;
        //Grab from recycle pool
        if (recyclePool.Count > 0)
        {
            chunk = recyclePool.Dequeue();
            chunk.chunkPosition = chunkKey.position;
            chunk.lod = chunkKey.lod;
        }
        //else create a brand new chunk, set up
        //This only happens on world startup
        //After that, there's always chunks in recycle pool
        else
        {
            //Create chunkObject
            chunk = CreateChunk(chunkKey);
            //Set up components of the game object
            chunk.SetUp(material);
        }
        chunkDictionary.Add(chunkKey, chunk);
        existingChunks.Add(chunkKey);
        //Update mesh with marching cubes in compute shader
        //See line 476
        UpdateChunkMesh(chunk);


    }

    //Creates chunk game object
    //Credit to Sebastian Lague for this function
    //https://github.com/SebLague/Marching-Cubes.git
    Chunk CreateChunk(ChunkKey chunkKey)
    {
        GameObject chunkGameObject = new GameObject($"Chunk ({chunkKey.position.x}, {chunkKey.position.y}, {chunkKey.position.z})");
        chunkGameObject.transform.parent = chunkHolder.transform;
        Chunk chunk = chunkGameObject.AddComponent<Chunk>();
        chunk.chunkPosition = chunkKey.position;
        chunk.lod = chunkKey.lod;
        chunk.generateCollider = true;
        return chunk;
    }


    //=========================================================
    //===============COMPUTE SHADER DISPATCH===================              
    //=========================================================
    //Updates chunks mesh by running compute shader
    //Compute shader sends back buffer of Triangle structs
    //Again, thanks to Sebastian Lague for heavily inspiring this function
    void UpdateChunkMesh(Chunk chunk)
    {
        uint[] zero = { 0 };
        triCountBuffer.SetData(zero); // sets triangleCount to 0

        shader.SetBuffer(0, "triangles", triangleBuffer);
        shader.SetBuffer(0, "triangleCount", triCountBuffer);

        shader.SetInts("chunkPosition", chunk.chunkPosition.x, chunk.chunkPosition.y, chunk.chunkPosition.z);
        shader.SetInt("lod", chunk.lod);
        shader.SetInt("chunkSize", chunkSize);
        shader.SetFloat("isoLevel", isoLevel);
        shader.SetInt("chunkStep", chunkStep);
        shader.SetInt("seed", seed);

        shader.Dispatch(0, threadGroup, threadGroup, threadGroup);

        //Get number of triangles in the triangle buffer
        int[] triCountArray = { 0 };
        triCountBuffer.GetData(triCountArray);
        int numTris = triCountArray[0];

        // Get triangle data from shader
        Triangle[] triangles = new Triangle[numTris];
        triangleBuffer.GetData(triangles, 0, 0, numTris);

        //Each triangle is 3 vertices
        int numVertices = numTris * 3;

        //These will become mesh.vertices and mesh.triangles
        Vector3[] vertices = new Vector3[numVertices];
        int[] meshTriangles = new int[numVertices];

        //becomes mesh.colors
        Color[] colors = new Color[numVertices];

        //smooth normals become mesh.normals
        Vector3[] normals = new Vector3[numVertices];

        //Iterate through each triangle struct and separate components
        for (int i = 0; i < numTris; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                int index = i * 3 + j;
                meshTriangles[index] = index;
                vertices[index] = triangles[i].GetVertex(j);
                colors[index] = (Color)triangles[i].GetColor(j);
                normals[index] = triangles[i].GetNormal(j);
            }
        }

        chunk.mesh.Clear();

        chunk.mesh.vertices = vertices;
        chunk.mesh.triangles = meshTriangles;
        chunk.mesh.colors = colors;
        chunk.mesh.normals = normals;

        chunk.meshCollider.enabled = false;

        //If mesh has vertices, assign it. Otherwise, clear it.
        //I was getting errors where I was assigning colliders to empty meshes
        //It didn't seem to be affecting gameplay but errors were annoying so I fixed it
        if (chunk.mesh.vertexCount > 0)
        {
            chunk.meshCollider.sharedMesh = chunk.mesh;
        }
        else
        {
            chunk.meshCollider.sharedMesh = null;
        }

        chunk.meshCollider.enabled = true;
    }

    //=========================================================
    //===============Garbage Cleanup===========================                  
    //=========================================================
    //Prevent memory leak
    void ReleaseBuffers()
    {
        triangleBuffer.Release();
        triCountBuffer.Release();
    }


    //self explanatory
    void ReleaseData()
    {
        if (chunkInfos.IsCreated) chunkInfos.Dispose();
        if (newChunks.IsCreated) newChunks.Dispose();
        if (oldChunks.IsCreated) oldChunks.Dispose();
        if (chunksToEnable.IsCreated) chunksToEnable.Dispose();
        if (positionHashSet.IsCreated) positionHashSet.Dispose();
        if (existingChunks.IsCreated) existingChunks.Dispose();
        if (chunksToDisable.IsCreated) chunksToDisable.Dispose();
    }

    //Cleanup
    void OnApplicationQuit()
    {
        ReleaseBuffers();
        ReleaseData();
        //Destroys all chunks and their components
        Destroy(chunkHolder);
    }


    //=========================================================
    //===============Structs===================================                  
    //=========================================================
    [BurstCompile]
    [GenerateTestsForBurstCompatibility]
    public struct ChunkKey : IEquatable<ChunkKey>
    {
        public int3 position;
        public int lod;

        public ChunkKey(int3 position, int lod)
        {
            this.position = position;
            this.lod = lod;
        }

        public bool Equals(ChunkKey other)
        {
            return position.Equals(other.position) && lod == other.lod;
        }

        public override bool Equals(object obj)
        {
            return obj is ChunkKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (position.GetHashCode() * 397) ^ lod;
        }

    }

    //Struct used in compute shader
    //Prevents race condition where threads place vertices out of order
    //Thanks to Sebastian Lague for this idea
    //There was another youtuber who did this too, I forget who it was though
    //I added normals and colors
    struct Triangle
    {
        public Vector3 vertexA;
        public Vector3 vertexB;
        public Vector3 vertexC;

        public Vector3 normalA;
        public Vector3 normalB;
        public Vector3 normalC;

        public Vector4 colorA;
        public Vector4 colorB;
        public Vector4 colorC;

        public Vector3 GetVertex(int i)
        {
            switch (i)
            {
                case 0: return vertexA;
                case 1: return vertexB;
                default: return vertexC;
            }
        }


        public Vector4 GetColor(int i)
        {
            switch (i)
            {
                case 0: return colorA;
                case 1: return colorB;
                default: return colorC;
            }
        }

        public Vector3 GetNormal(int i)
        {
            switch (i)
            {
                case 0: return normalA;
                case 1: return normalB;
                default: return normalC;
            }
        }

    }



    //=========================================================
    //===============Miscellaneous=============================                 
    //=========================================================
    [BurstCompile]
    public static int3 CalculateChunkPosition(float3 playerPosition, int chunkSize, int lod)
    {
        return (int3)math.round(playerPosition / (chunkSize << lod));
    }


    //Fills chunkInfos with all chunks that should exist, centered around world origin
    //Chunks with lower indices are closer to the origin
    //Chunks are added in rings around origin
    //That way when chunks are enabled, chunks closest to the origin/player are enabled first
    void FillChunkInfos(NativeParallelHashSet<ChunkKey> positionHashSet, NativeArray<ChunkKey> chunkInfos)
    {
        int index = 0;

        for (int lod = 0; lod < lodLevels; lod++)
        {
            for (int ring = 1; ring <= viewDistance; ring++)
            {
                for (int x = -ring; x < ring; x++)
                {
                    for (int y = -ring; y < ring; y++)
                    {
                        for (int z = -ring; z < ring; z++)
                        {
                            if (lod != 0 && ring <= ((viewDistance / 2) - 1))
                            {
                                continue;
                            }
                            bool outsideRing =
                                x > -ring && x < ring - 1 &&
                                y > -ring && y < ring - 1 &&
                                z > -ring && z < ring - 1;
                            if (outsideRing)
                            {
                                continue;
                            }
                            ChunkKey key = new ChunkKey(new int3(x, y, z), lod);
                            chunkInfos[index++] = key;
                            positionHashSet.Add(key);
                        }
                    }
                }
            }
        }

    }






    /*
    //This was for looking at meshes and colliders
    //Not important
    void OnDrawGizmos()
    {
        if (chunkDictionary == null) return;

        foreach (var kvp in chunkDictionary)
        {
            Chunk chunk = kvp.Value;

            if (chunk.generateCollider && chunk.meshCollider != null && chunk.meshCollider.sharedMesh != null)
            {
                // Choose a distinct color for collider
                Gizmos.color = new Color(1f, 0f, 0f, 0.5f); // red, semi-transparent

                Gizmos.DrawWireMesh(
                    chunk.meshCollider.sharedMesh,
                    chunk.transform.position,
                    chunk.transform.rotation,
                    chunk.transform.lossyScale
                );
            }
        }
    }*/




}