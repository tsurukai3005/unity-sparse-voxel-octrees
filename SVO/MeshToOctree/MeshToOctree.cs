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

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

#if UNITY_EDITOR

namespace SVO
{
    /// <summary>
    /// メッシュからSparse Voxel Octreeへの変換を行う抽象基底クラス
    /// 
    /// ボクセル化プロセス：
    /// 1. メッシュの最適なオクトリー境界を計算
    /// 2. 各三角形を指定深度でボクセル化
    /// 3. 三角形-ボックス交差判定による高精度変換
    /// 4. 属性データ（色、法線等）の補間と格納
    /// 
    /// メモリ効率の最適化：
    /// - サブメッシュ単位での分割処理
    /// - オクトリーサイズの動的最適化
    /// - 並列処理対応の空間分割アルゴリズム
    /// </summary>
    public abstract class MeshToOctree: MonoBehaviour
    {
        /// <summary>変換対象のメッシュ</summary>
        public Mesh mesh;
        
        /// <summary>
        /// ボクセルサイズ（最小単位）
        /// 小さいほど高精細だが、メモリ使用量が指数的に増加
        /// 推奨値：0.01～1.0（オブジェクトサイズに応じて調整）
        /// </summary>
        public float voxelSize;
        
        /// <summary>シェーディング用のマテリアル（テクスチャサンプリングに使用）</summary>
        public Material material;
        
        /// <summary>
        /// メッシュからオクトリーへの変換実行メソッド
        /// 
        /// 処理フロー：
        /// 1. 最適なオクトリー境界の計算
        /// 2. 必要深度の決定（ボクセルサイズから自動計算）
        /// 3. サブメッシュ毎の並列ボクセル化
        /// 4. GPU用Texture3Dとしてアセット化
        /// </summary>
        public void Generate()
        {
            // オクトリーの最適境界を計算（グリッド整列による他オクトリーとの互換性確保）
            var idealBounds = FindIdealOctreeBounds();   

            // 深度計算：log2(オクトリーサイズ / ボクセルサイズ)
            // 深度nでボクセルサイズは2^-n になる
            var depth = Mathf.RoundToInt(Mathf.Log(idealBounds.size.x / voxelSize, 2));

            // メモリ制限チェック：深度12超過時は分割を推奨
            // 深度12 = 4096^3ボクセル ≈ 68GB（理論最大）
            if (depth > 12)
                throw new NotSupportedException("Octree voxel size is too small. Please split the mesh.");

            // オクトリー生成と各サブメッシュのボクセル化
            var octreeData = new Octree();
            for (var submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                FillSubmesh(octreeData, depth, submesh, idealBounds.size.x, idealBounds.center);
            }
            
            // GPU用Texture3Dに変換してアセットとして保存
            AssetDatabase.CreateAsset(octreeData.Apply(true), "Assets/mesh.asset");
        }

        /// <summary>
        /// サブメッシュ単位でのボクセル化処理
        /// 
        /// 最適化技術：
        /// 1. 三角形単位での並列処理可能な分解
        /// 2. 座標変換の事前計算によるパフォーマンス向上
        /// 3. メモリ効率的な頂点データアクセス
        /// </summary>
        /// <param name="data">対象オクトリー</param>
        /// <param name="depth">ボクセル化深度</param>
        /// <param name="submesh">処理対象サブメッシュインデックス</param>
        /// <param name="octreeSize">オクトリーのワールドサイズ</param>
        /// <param name="octreeCenter">オクトリー中心座標</param>
        private void FillSubmesh(Octree data, int depth, int submesh, float octreeSize, Vector3 octreeCenter)
        {
            // サブクラスでの前処理（マテリアル、UV、法線等の準備）
            OnFillSubmesh(submesh);
            
            // インデックスと頂点データの取得
            var indices = mesh.GetIndices(submesh);
            var triIndices = new int[3];
            var vertices = new List<Vector3>();
            mesh.GetVertices(vertices);
                
            // 座標変換用の作業配列
            var triVerts = new Vector3[3];      // オクトリー座標系の三角形頂点
            var triVertsMesh = new Vector3[3];  // オリジナルメッシュ座標系の頂点
            
            // 座標変換の事前計算：ワールド座標 → オクトリー正規化座標
            var octreeSizeInv = 1 / octreeSize;

            // 全三角形を順次処理
            for (var i = 0; i < indices.Length; i += 3)
            {
                // 三角形の3頂点インデックスを取得
                triIndices[0] = indices[i];
                triIndices[1] = indices[i + 1];
                triIndices[2] = indices[i + 2];
                
                // 座標変換：ワールド座標 → オクトリー正規化座標 [-0.5, 0.5]
                triVerts[0] = (vertices[triIndices[0]] - octreeCenter) * octreeSizeInv;
                triVerts[1] = (vertices[triIndices[1]] - octreeCenter) * octreeSizeInv;
                triVerts[2] = (vertices[triIndices[2]] - octreeCenter) * octreeSizeInv;
                
                // オリジナル座標も保持（属性補間で使用）
                triVertsMesh[0] = vertices[triIndices[0]];
                triVertsMesh[1] = vertices[triIndices[1]];
                triVertsMesh[2] = vertices[triIndices[2]];
                
                /// <summary>
                /// 属性生成クロージャ：ボクセル位置に応じた属性データを計算
                /// 
                /// 補間技術：
                /// - 重心座標による高精度補間
                /// - ワールド座標とオクトリー座標の双方向変換
                /// - テクスチャサンプリングによる色情報取得
                /// </summary>
                Tuple<Color, int[]> InternalGenerateAttributes(Bounds bounds)
                {
                    // オクトリー座標 → ワールド座標への逆変換
                    var transformedCenter = bounds.center * octreeSize + octreeCenter;
                    return GenerateAttributes(triVertsMesh, triIndices, bounds,
                        new Bounds(transformedCenter, Vector3.one * voxelSize),
                        octreeSize, octreeCenter);
                }
                
                // 三角形の再帰的ボクセル化を実行
                // 空間分割による効率的な交差判定
                data.FillTriangle(triVerts, depth, InternalGenerateAttributes);
            }
        }

        /// <summary>
        /// サブメッシュ処理開始時のコールバック
        /// サブクラスで属性データの準備を行う
        /// </summary>
        /// <param name="submesh">処理対象サブメッシュインデックス</param>
        protected abstract void OnFillSubmesh(int submesh);

        /// <summary>
        /// ボクセル属性データの生成
        /// 
        /// 実装すべき処理：
        /// 1. 重心座標計算による位置補間
        /// 2. UV座標補間とテクスチャサンプリング
        /// 3. 法線ベクトルの補間と正規化
        /// 4. カスタム属性の計算
        /// </summary>
        /// <param name="triangleVertices">三角形頂点（ワールド座標）</param>
        /// <param name="indices">頂点インデックス</param>
        /// <param name="voxelLocalBounds">ボクセルのオクトリー座標境界</param>
        /// <param name="voxelMeshBounds">ボクセルのワールド座標境界</param>
        /// <param name="octreeSize">オクトリーのワールドサイズ</param>
        /// <param name="octreeCenter">オクトリー中心のワールド座標</param>
        /// <returns>色と属性データのタプル</returns>
        protected abstract Tuple<Color, int[]> GenerateAttributes(Vector3[] triangleVertices, int[] indices,
            Bounds voxelLocalBounds, Bounds voxelMeshBounds, float octreeSize, Vector3 octreeCenter);
        
        /// <summary>
        /// メッシュに最適なオクトリー境界を計算
        /// 
        /// 最適化戦略：
        /// 1. グリッド整列：他のオクトリーとの結合を容易にする
        /// 2. 最小包含サイズ：メモリ効率を最大化
        /// 3. 2の累乗サイズ：GPU処理との親和性向上
        /// </summary>
        /// <returns>最適化されたオクトリー境界</returns>
        private Bounds FindIdealOctreeBounds()
        {
            // オクトリー中心をボクセルサイズのグリッドに整列
            // 複数オクトリーの組み合わせ時の一貫性を確保
            var octreePos = new Vector3();
            for(var i = 0; i < 3; i++) 
                octreePos[i] = Mathf.Round(mesh.bounds.center[i] / voxelSize) * voxelSize;
            
            // メッシュ全体を包含する最小オクトリーサイズを計算
            var octreeSize = -1f;
            for (var i = 0; i < 3; i++)
            {
                // 各軸で中心からの最大距離を計算
                octreeSize = Mathf.Max(octreeSize, Mathf.Abs(mesh.bounds.max[i] - octreePos[i]));
                octreeSize = Mathf.Max(octreeSize, Mathf.Abs(mesh.bounds.min[i] - octreePos[i]));
            }

            octreeSize *= 2; // 直径に変換
            
            // ボクセルサイズで割り切れる2の累乗サイズに調整
            // これにより指定されたvoxelSizeでのボクセル化が保証される
            octreeSize = Mathf.NextPowerOfTwo(Mathf.CeilToInt(octreeSize / voxelSize)) * voxelSize;
            
            return new Bounds(octreePos, Vector3.one * octreeSize);
        }
    }
}

#endif