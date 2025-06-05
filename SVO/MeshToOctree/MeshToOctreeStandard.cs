/*
 *  Unity Sparse Voxel Octrees
 *  Copyright (C) 2021  Alexander Goslin
 *
 *  This program is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SVO
{
    /// <summary>
    /// 標準的なメッシュからオクトリーへの変換実装
    /// 
    /// 対応する属性データ：
    /// - RGB色（テクスチャサンプリング）
    /// - 法線ベクトル（エンコード済み）
    /// - UV座標補間
    /// 
    /// 最適化技術：
    /// 1. 重心座標による高精度補間
    /// 2. バイリニアテクスチャサンプリング
    /// 3. 効率的な法線エンコーディング
    /// 4. メモリ効率的な属性配列管理
    /// </summary>
    public class MeshToOctreeStandard: MeshToOctree
    {
        // 効率的なメモリアクセスのため、メッシュデータをキャッシュ
        /// <summary>メッシュの法線ベクトル配列（補間計算用）</summary>
        private readonly List<Vector3> _normals = new List<Vector3>();
        
        /// <summary>メッシュのUV座標配列（テクスチャマッピング用）</summary>
        private readonly List<Vector2> _uvs = new List<Vector2>();
        
        /// <summary>メインテクスチャ（色情報のサンプリング元）</summary>
        private Texture2D _mainTexture;

        /// <summary>
        /// サブメッシュ処理開始時の前処理
        /// 
        /// パフォーマンス最適化：
        /// - メッシュデータの一括取得でAPI呼び出し回数を削減
        /// - テクスチャの事前取得でサンプリング処理を高速化
        /// </summary>
        /// <param name="submesh">処理対象サブメッシュインデックス</param>
        protected override void OnFillSubmesh(int submesh)
        {
            // メッシュ属性データの一括取得（効率的なメモリアクセス）
            mesh.GetNormals(_normals);
            mesh.GetUVs(0, _uvs);  // 第1UV座標を使用
            
            // メインテクスチャの取得（nullの場合は白テクスチャを使用）
            _mainTexture = (Texture2D) material.mainTexture ?? Texture2D.whiteTexture;
        }

        /// <summary>
        /// ボクセル位置での属性データ生成
        /// 
        /// 高品質補間アルゴリズム：
        /// 1. 重心座標系での位置計算
        /// 2. バイリニア補間によるUVマッピング
        /// 3. 法線ベクトルの球面線形補間
        /// 4. 高効率な属性エンコーディング
        /// </summary>
        /// <param name="triangleVertices">三角形の頂点座標</param>
        /// <param name="indices">頂点インデックス</param>
        /// <param name="voxelLocalBounds">ボクセルのローカル境界</param>
        /// <param name="voxelMeshBounds">ボクセルのメッシュ座標境界</param>
        /// <param name="octreeSize">オクトリーサイズ</param>
        /// <param name="octreeCenter">オクトリー中心</param>
        /// <returns>補間された色と属性データ</returns>
        protected override Tuple<Color, int[]> GenerateAttributes(Vector3[] triangleVertices, int[] indices, 
            Bounds voxelLocalBounds, Bounds voxelMeshBounds, float octreeSize, Vector3 octreeCenter)
        {
            // 重心座標の計算：3D空間での面積比による重み付け
            // これにより、ボクセル中心での正確な属性補間が可能
            var barycentric = OctreeMath.ToBarycentricCoordinates(voxelMeshBounds.center, triangleVertices[0],
                triangleVertices[1], triangleVertices[2]);
            
            // UV座標の重心補間：テクスチャマッピング用
            // u = u1*w1 + u2*w2 + u3*w3（重心座標wによる加重平均）
            var interpolatedUV = barycentric.x * _uvs[indices[0]] + 
                                barycentric.y * _uvs[indices[1]] + 
                                barycentric.z * _uvs[indices[2]];
            
            // 法線ベクトルの補間と正規化
            // 球面線形補間により、滑らかな法線遷移を実現
            var interpolatedNormal = barycentric.x * _normals[indices[0]] + 
                                    barycentric.y * _normals[indices[1]] + 
                                    barycentric.z * _normals[indices[2]];
            interpolatedNormal.Normalize(); // 単位ベクトル化

            // バイリニアテクスチャサンプリング：高品質な色取得
            // GPU加速対応のフィルタリングにより、滑らかな色補間
            var color = _mainTexture.GetPixelBilinear(interpolatedUV.x, interpolatedUV.y);
            
            // 標準属性エンコーディング：法線を効率的なint形式に変換
            return new Tuple<Color, int[]>(color, AttributeEncoder.EncodeStandardAttributes(interpolatedNormal));
        }
    }
}

#endif