using UnityEngine;
using UnityEditor;

namespace Editor {
    public class TextureImportModifier : ScriptableObject
    {
        // 压缩
        [MenuItem ("TextureImportModifier/一键设置纹理")]  // 不压缩纹理。
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
                if (textureTypeName.Equals("A")) {
                    textureImporter.textureType = TextureImporterType.Default;
                    textureImporter.sRGBTexture = true;
                    platformSettings.maxTextureSize = 512;
                } 
                else if (textureTypeName.Equals("N")) {
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
                platformSettings.compressionQuality = 100;
                textureImporter.SetPlatformTextureSettings(platformSettings);  
                textureImporter.textureCompression = TextureImporterCompression.CompressedHQ;
                AssetDatabase.ImportAsset(path);
            }
        }
        
        static Object[] GetSelectedTextures() { 
            return Selection.GetFiltered(typeof(Texture2D), SelectionMode.DeepAssets); // 如果选择包含文件夹，还包括文件层次结构中该文件夹中的所有资源和子文件夹。
        }
    }
}

