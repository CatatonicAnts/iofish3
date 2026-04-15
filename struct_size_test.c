#include <stdio.h>
#include "code/renderercommon/tr_types.h"

int main() {
    printf("=== refEntity_t Layout ===\n");
    printf("sizeof(refEntity_t) = %zu\n\n", sizeof(refEntity_t));
    
    refEntity_t r = {0};
    printf("Offset of reType: %zu\n", (size_t)&r.reType - (size_t)&r);
    printf("Offset of renderfx: %zu\n", (size_t)&r.renderfx - (size_t)&r);
    printf("Offset of hModel: %zu\n", (size_t)&r.hModel - (size_t)&r);
    printf("Offset of lightingOrigin: %zu\n", (size_t)&r.lightingOrigin - (size_t)&r);
    printf("Offset of shadowPlane: %zu\n", (size_t)&r.shadowPlane - (size_t)&r);
    printf("Offset of axis: %zu\n", (size_t)&r.axis - (size_t)&r);
    printf("Offset of nonNormalizedAxes: %zu\n", (size_t)&r.nonNormalizedAxes - (size_t)&r);
    printf("Offset of origin: %zu\n", (size_t)&r.origin - (size_t)&r);
    printf("Offset of frame: %zu\n", (size_t)&r.frame - (size_t)&r);
    printf("Offset of oldorigin: %zu\n", (size_t)&r.oldorigin - (size_t)&r);
    printf("Offset of oldframe: %zu\n", (size_t)&r.oldframe - (size_t)&r);
    printf("Offset of backlerp: %zu\n", (size_t)&r.backlerp - (size_t)&r);
    printf("Offset of skinNum: %zu\n", (size_t)&r.skinNum - (size_t)&r);
    printf("Offset of customSkin: %zu\n", (size_t)&r.customSkin - (size_t)&r);
    printf("Offset of customShader: %zu\n", (size_t)&r.customShader - (size_t)&r);
    printf("Offset of shaderRGBA: %zu\n", (size_t)&r.shaderRGBA - (size_t)&r);
    printf("Offset of shaderTexCoord: %zu\n", (size_t)&r.shaderTexCoord - (size_t)&r);
    printf("Offset of shaderTime: %zu\n", (size_t)&r.shaderTime - (size_t)&r);
    printf("Offset of radius: %zu\n", (size_t)&r.radius - (size_t)&r);
    printf("Offset of rotation: %zu\n", (size_t)&r.rotation - (size_t)&r);
    
    printf("\n=== refdef_t Layout ===\n");
    printf("sizeof(refdef_t) = %zu\n\n", sizeof(refdef_t));
    
    refdef_t rd = {0};
    printf("Offset of x: %zu\n", (size_t)&rd.x - (size_t)&rd);
    printf("Offset of y: %zu\n", (size_t)&rd.y - (size_t)&rd);
    printf("Offset of width: %zu\n", (size_t)&rd.width - (size_t)&rd);
    printf("Offset of height: %zu\n", (size_t)&rd.height - (size_t)&rd);
    printf("Offset of fov_x: %zu\n", (size_t)&rd.fov_x - (size_t)&rd);
    printf("Offset of fov_y: %zu\n", (size_t)&rd.fov_y - (size_t)&rd);
    printf("Offset of vieworg: %zu\n", (size_t)&rd.vieworg - (size_t)&rd);
    printf("Offset of viewaxis: %zu\n", (size_t)&rd.viewaxis - (size_t)&rd);
    printf("Offset of time: %zu\n", (size_t)&rd.time - (size_t)&rd);
    printf("Offset of rdflags: %zu\n", (size_t)&rd.rdflags - (size_t)&rd);
    printf("Offset of areamask: %zu\n", (size_t)&rd.areamask - (size_t)&rd);
    printf("Offset of text: %zu\n", (size_t)&rd.text - (size_t)&rd);
    
    return 0;
}
