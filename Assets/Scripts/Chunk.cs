//Thanks to Sebastian Lague for heavily inspiring this code
//My chunk class is basically his with minor changes
//https://github.com/SebLague/Marching-Cubes.git

using System;
using Unity.Mathematics;
using UnityEngine;

//Raghav Dukkipati

public class Chunk : MonoBehaviour
{
    //Note chunkPosition is in chunk coordinates
    //Have to multiply by chunk size to get world coordinates
    public int3 chunkPosition;
    public int lod;
    public bool generateCollider;

    public Mesh mesh;

    MeshFilter meshFilter;
    MeshRenderer meshRenderer;
    public MeshCollider meshCollider;


    //Meshes are not automatically destroyed
    //Gotta do it myself
    void OnDestroy()
    {
        if (mesh != null)
        {
            Destroy(mesh);
        }
    }

    //No longer use this function
    /*
    public void DestroyChunk()
    {
        Destroy(mesh);
        Destroy(gameObject);
    }
    */

    public void SetUp(Material material)
    {
        if (meshFilter == null)
        {
            meshFilter = gameObject.AddComponent<MeshFilter>();
        }

        if (meshRenderer == null)
        {
            meshRenderer = gameObject.AddComponent<MeshRenderer>();
        }

        if (meshCollider == null && generateCollider)
        {
            meshCollider = gameObject.AddComponent<MeshCollider>();
        }


        if (mesh == null)
        {
            mesh = new Mesh();
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            meshFilter.sharedMesh = mesh;
        }

        if (generateCollider)
        {
            if (meshCollider.sharedMesh == null)
            {
                meshCollider.sharedMesh = mesh;
            }
            // force update
            meshCollider.enabled = false;
            meshCollider.enabled = true;
        }



        meshRenderer.material = material;
    }

}