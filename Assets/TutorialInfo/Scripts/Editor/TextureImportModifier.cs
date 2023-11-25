using System;
using UnityEngine;
using UnityEditor;
using UnityEditor.AssetImporters;
using Object = UnityEngine.Object;

namespace TutorialInfo.Scripts.Editor {
    public class TextureImportModifier : AssetPostprocessor
    {
        private void OnPreprocessTexture() {
            var textureImporter = (TextureImporter)assetImporter;
            
            string path = textureImporter.assetPath;
            string p = assetPath;
            AssetImportContext con =  context;
            Debug.Log("path:" + path);
            Debug.Log("p:" + p);
            if (path.StartsWith("Assets/ExampleAssets/Textures/123")) {
                string texName = path.Split('.')[0];
                // Debug.Log("texName:" + texName);
                TextureImporterPlatformSettings platformSettings = textureImporter.GetPlatformTextureSettings("Android");
                if (texName.EndsWith("Tex_A")) {
                    textureImporter.textureType = TextureImporterType.Default;
                    textureImporter.sRGBTexture = true;
                }
                else if (texName.EndsWith("Tex_N")) {
                    textureImporter.textureType = TextureImporterType.NormalMap;
                    textureImporter.sRGBTexture = false;
                }
                else {
                    textureImporter.textureType = TextureImporterType.Default;
                    textureImporter.sRGBTexture = false;
                }
        
                textureImporter.isReadable = false;
                textureImporter.mipmapEnabled = true; // 默认设置为true
                if (textureImporter.mipmapEnabled) {
                    textureImporter.streamingMipmaps = true;
                }

                textureImporter.anisoLevel = 0;
        
                platformSettings.name = "Android";
                platformSettings.overridden = true;
                platformSettings.maxTextureSize = 512;
                platformSettings.format = TextureImporterFormat.ASTC_6x6;
                platformSettings.compressionQuality = 100; // Best
                textureImporter.SetPlatformTextureSettings(platformSettings);
            }
        }

        
        // -------------------------------------------------------------------
        
        
        // 压缩
        [MenuItem ("Texture Import Tool/一键设置纹理参数")]  // 不压缩纹理。
        static void ChangeTextureState() {
            Object[] textures = GetSelectedTextures(); 
            Selection.objects = new Object[0];
            foreach (Texture2D texture in textures) {
                string path = AssetDatabase.GetAssetPath(texture);
                string texName = "";
                string[] tmpStr =  path.Split("/");
                if (tmpStr.Length > 0) {
                    string textureFullName = tmpStr[tmpStr.Length-1];
                    texName = textureFullName.Split(".")[0];
                }

                string textureTypeName = "";
                if (!texName.Equals("")) {
                    string[] tmpName = texName.Split("_");
                    textureTypeName = tmpName[tmpName.Length - 1];
                }

                TextureImporter textureImporter = AssetImporter.GetAtPath(path) as TextureImporter;
                TextureImporterPlatformSettings platformSettings = textureImporter.GetPlatformTextureSettings("Android");
                if (textureTypeName.ToUpper().Equals("A")) {
                    textureImporter.textureType = TextureImporterType.Default;
                    textureImporter.sRGBTexture = true;
                    platformSettings.maxTextureSize = 512;
                } 
                else if (textureTypeName.ToUpper().Equals("N")) {
                    textureImporter.textureType = TextureImporterType.NormalMap;
                    textureImporter.sRGBTexture = false;
                    platformSettings.maxTextureSize = 256;
                } 
                else if (textureTypeName.ToUpper().Equals("MRA") || textureTypeName.ToUpper().Equals("n")) {
                    textureImporter.textureType = TextureImporterType.NormalMap;
                    textureImporter.sRGBTexture = false;
                    platformSettings.maxTextureSize = 256;
                }
                else {
                    textureImporter.textureType = TextureImporterType.Default;
                    textureImporter.sRGBTexture = false;
                    platformSettings.maxTextureSize = 256;
                }
                    
                textureImporter.isReadable = false;
                textureImporter.mipmapEnabled = true;  // 默认设置为true
                if (textureImporter.mipmapEnabled) {
                    textureImporter.streamingMipmaps = true;
                }

                platformSettings.name = "Android";
                platformSettings.format = TextureImporterFormat.ASTC_6x6;
                platformSettings.compressionQuality = 100;  // Best
                textureImporter.SetPlatformTextureSettings(platformSettings);  
                AssetDatabase.ImportAsset(path);
            }
        }
        
        static Object[] GetSelectedTextures() { 
            return Selection.GetFiltered(typeof(Texture2D), SelectionMode.DeepAssets); // 如果选择包含文件夹，还包括文件层次结构中该文件夹中的所有资源和子文件夹。
        }
    }
}

