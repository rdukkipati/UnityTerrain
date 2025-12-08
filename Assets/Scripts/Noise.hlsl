float RemapNoiseToHeight(float n)
//Raghav Dukkipati
{
    

    if (n <= 0.0)
    {
        
        float t = -n; 
        return lerp(0.0, 20.0, t);
    }
    else
    {
        
        float t = n;
        return lerp(20.0, 8000.0, t);
    }
}