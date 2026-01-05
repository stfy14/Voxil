float GetCornerOcclusion(ivec3 pos, ivec3 side1, ivec3 side2) {
    bool s1 = IsSolid(pos + side1), s2 = IsSolid(pos + side2), c = IsSolid(pos + side1 + side2);
    if (s1 && s2) return 3.0;
    return float(s1) + float(s2) + float(c);
}

float CalculateAO(vec3 hitPos, vec3 normal) {
    ivec3 ipos = ivec3(floor(hitPos + normal * 0.01)), n = ivec3(normal);
    vec3 localPos = hitPos - vec3(ipos);
    ivec3 t, b; vec2 uvSurf;
    if (abs(n.y) > 0.5) { t = ivec3(1, 0, 0); b = ivec3(0, 0, 1); uvSurf = localPos.xz; }
    else if (abs(n.x) > 0.5) { t = ivec3(0, 0, 1); b = ivec3(0, 1, 0); uvSurf = localPos.zy; }
    else { t = ivec3(1, 0, 0); b = ivec3(0, 1, 0); uvSurf = localPos.xy; }
    uvSurf = fract(uvSurf);
    float occ00 = GetCornerOcclusion(ipos, -t, -b), occ10 = GetCornerOcclusion(ipos, t, -b);
    float occ01 = GetCornerOcclusion(ipos, -t, b), occ11 = GetCornerOcclusion(ipos, t, b);
    vec2 smoothUV = uvSurf * uvSurf * (3.0 - 2.0 * uvSurf);
    float finalOcc = mix(mix(occ00, occ10, smoothUV.x), mix(occ01, occ11, smoothUV.x), smoothUV.y);
    return clamp(pow(0.8, finalOcc) + (IGN(gl_FragCoord.xy) - 0.5) / 64.0, 0.0, 1.0);
}

// Мягкие тени
float CalculateSoftShadow(vec3 ro, vec3 normal, vec3 sunDir) {
    vec3 shadowOrigin = ro + normal * 0.005;
    vec3 up = abs(sunDir.y) < 0.999 ? vec3(0, 1, 0) : vec3(0, 0, 1);
    vec3 right = normalize(cross(sunDir, up));
    up = cross(right, sunDir);

    vec2 offsets[8] = vec2[](
    vec2(0.1, 0.1), vec2(-0.1, -0.1), vec2(0.3, 0.4), vec2(-0.3, -0.4),
    vec2(0.2, -0.6), vec2(-0.2, 0.6), vec2(0.7, 0.0), vec2(-0.7, 0.0)
    );

    float currentShadow = 0.0;
    float diskRadius = 0.04;
    float noiseVal = random(gl_FragCoord.xy);
    float angle = noiseVal * 6.283185;
    float s = sin(angle); float c = cos(angle);
    float jitteredRadius = diskRadius * (0.5 + 1.0 * noiseVal);

    // Bounds for shadow ray
    ivec3 bMin = ivec3(uBoundMinX, uBoundMinY, uBoundMinZ) * 16;
    ivec3 bMax = ivec3(uBoundMaxX, uBoundMaxY, uBoundMaxZ) * 16;

    for (int k = 0; k < 8; k++) {
        vec2 rotatedOffset = vec2(offsets[k].x * c - offsets[k].y * s, offsets[k].x * s + offsets[k].y * c);
        vec3 offsetDir = right * rotatedOffset.x + up * rotatedOffset.y;
        vec3 shadowDir = normalize(sunDir + offsetDir * jitteredRadius);
        if (abs(shadowDir.x) < 1e-6) shadowDir.x = 1e-6;

        ivec3 sMapPos = ivec3(floor(shadowOrigin));
        ivec3 sStepDir = ivec3(sign(shadowDir));
        vec3 sDeltaDist = abs(1.0 / shadowDir);
        vec3 sSideDist = (sign(shadowDir) * (vec3(sMapPos) - shadowOrigin) + (0.5 + 0.5 * sign(shadowDir))) * sDeltaDist;

        bool hit = false;
        float hitDist = 0.0;
        ivec3 sCachedChunkCoord = ivec3(-999999); int sCachedChunkIdx = -1;

        for (int step = 0; step < 60; step++) {
            if (min(sSideDist.x, min(sSideDist.y, sSideDist.z)) > 60.0) break;
            if (any(lessThan(sMapPos, bMin)) || any(greaterThanEqual(sMapPos, bMax))) break;

            ivec3 chunkCoord = sMapPos >> 4;
            if (chunkCoord != sCachedChunkCoord) {
                sCachedChunkCoord = chunkCoord;
                sCachedChunkIdx = imageLoad(uPageTable, chunkCoord & (PAGE_TABLE_SIZE - 1)).r;
            }
            if (sCachedChunkIdx != -1) {
                ivec3 local = sMapPos & 15;
                int idx = local.x + 16 * (local.y + 16 * local.z);
                uint sMat = (packedVoxels[sCachedChunkIdx + (idx >> 2)] >> ((idx & 3) * 8)) & 0xFFu;
                if (sMat != 0u && sMat != 4u) { // 4u = Water (transparent for shadows mostly)
                                                hit = true;
                                                hitDist = distance(shadowOrigin, vec3(sMapPos) + 0.5);
                                                break;
                }
            }
            vec3 sMask = (sSideDist.x < sSideDist.y) ?
            ((sSideDist.x < sSideDist.z) ? vec3(1,0,0) : vec3(0,0,1)) :
            ((sSideDist.y < sSideDist.z) ? vec3(0,1,0) : vec3(0,0,1));
            sSideDist += sMask * sDeltaDist; sMapPos += ivec3(sMask) * sStepDir;
        }

        if (!hit) currentShadow += 1.0;
        else currentShadow += smoothstep(2.0, 25.0, hitDist) * 0.9;
    }

    float rawShadow = currentShadow / 8.0;
    float shadowFactor = smoothstep(0.0, 1.0, rawShadow);
    shadowFactor += (noiseVal - 0.5) * 0.05;
    return clamp(shadowFactor, 0.0, 1.0);
}