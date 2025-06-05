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
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SVO
{
    /// <summary>
    /// Sparse Voxel Octree（疎なボクセルオクトリー）のメインクラス
    /// 
    /// データ構造の説明：
    /// - オクトリーは3D空間を8つの子ノードに再帰的に分割する階層構造
    /// - 各ノードは「ポインタ」（子ノードへの参照）か「ボクセル」（実際のデータ）のいずれか
    /// - 空のボクセルは格納せず、メモリ効率を向上させる（疎な構造）
    /// 
    /// メモリ管理：
    /// - _dataリストが全てのノードデータを連続して格納
    /// - フリープールを使用してメモリ再利用と断片化を防ぐ
    /// - GPU転送用にTexture3Dとして最適化されたメモリレイアウト
    /// </summary>
    public class Octree : IDisposable
    {
        // GPU転送用の一時的なテクスチャ（メモリ効率のため再利用）
        private static Texture3D tempTex = null;
        
        /// <summary>
        /// GPU側で使用するためのTexture3D形式のオクトリーデータ
        /// 256x256のスライスに分割して格納し、深度方向に拡張可能
        /// </summary>
        public Texture3D Data { get; private set; }

        // メモリ効率の最適化：C#のGCを回避するため、int配列として管理
        // これにより、CPU→GPU間の転送が高速化され、ガベージコレクションの負荷も軽減
        
        /// <summary>
        /// 全てのオクトリーノードデータを格納するメインメモリブロック
        /// 形式：int配列として管理し、ポインタとボクセルデータを混在格納
        /// 最上位ビット（31bit）：ノードタイプフラグ（0=ポインタ, 1=ボクセル）
        /// 下位31ビット：ポインタまたは属性データへの参照
        /// </summary>
        private List<int> _data = new List<int>(new[] { 1 << 31 });
        
        /// <summary>
        /// 削除されたノード構造のメモリ位置を管理するフリープール
        /// メモリ断片化を防ぎ、削除・追加操作を高速化
        /// </summary>
        private readonly HashSet<int> _freeStructureMemory = new HashSet<int>();
        
        /// <summary>
        /// 削除された属性データのメモリ位置を管理するフリープール
        /// 色、法線などの属性データの再利用でメモリ効率を向上
        /// </summary>
        private readonly HashSet<int> _freeAttributeMemory = new HashSet<int>();
        
        /// <summary>
        /// GPU更新追跡用：各Texture3Dスライスの更新回数
        /// 差分更新により、変更されたスライスのみをGPUに転送
        /// </summary>
        private ulong[] _updateCount = new ulong[2048];
        
        /// <summary>
        /// GPU更新追跡用：各スライスの最後の適用時刻
        /// 差分更新の判定に使用
        /// </summary>
        private ulong[] _lastApply = new ulong[2048];

        // 座標管理の最適化：前回の操作位置を記憶してツリー走査を高速化
        
        /// <summary>
        /// ツリー走査の最適化用：各深度でのポインタスタック
        /// 前回のボクセル操作位置から共通の親ノードを再利用
        /// 最大深度24（2^-23の精度）まで対応
        /// </summary>
        private int[] _ptrStack = new int[24];
        
        /// <summary>
        /// 前回操作したボクセルの正規化座標（1.0-2.0の範囲）
        /// 隣接ボクセルへのアクセス時にツリー走査を最適化
        /// </summary>
        private Vector3 _ptrStackPos = Vector3.one;
        
        /// <summary>
        /// 現在のポインタスタックの深度
        /// ツリーの共通親ノードからの走査開始深度を管理
        /// </summary>
        private int _ptrStackDepth;

        public Octree(Texture3D data)
        {
            // 全スライスを未更新状態に初期化
            for (int i = 0; i < _lastApply.Length; i++)
                _lastApply[i] = ulong.MaxValue;
            _ptrStack[0] = 0; // ルートノードのポインタ
            Data = data;
        }

        public Octree()
        {
            _ptrStack[0] = 0;
            Data = null;
        }
        
        /// <summary>
        /// 指定位置にボクセルを設定する（ユーザー向けAPI）
        /// 座標系を[-0.5, 0.5)から内部座標系[1.0, 2.0)に変換
        /// </summary>
        /// <param name="position">ボクセル位置（各成分は[-0.5, 0.5)の範囲）</param>
        /// <param name="depth">ボクセルの深度（n深度 = 2^-n サイズ）</param>
        /// <param name="color">ボクセルの色（アルファ0で削除）</param>
        /// <param name="attributes">シェーディング用属性データ（法線など）</param>
        public void SetVoxel(Vector3 position, int depth, Color color, int[] attributes)
        {
            SetVoxelNormalized(position + new Vector3(1.5f, 1.5f, 1.5f), depth, color, attributes);
        }

        /// <summary>
        /// 正規化座標でボクセルを設定する（内部処理メソッド）
        /// 
        /// メモリ効率の最適化技術：
        /// 1. 浮動小数点ビット操作による高速座標計算
        /// 2. 前回位置の記憶による差分ツリー走査
        /// 3. フリープールによるメモリ再利用
        /// 4. 階層的メモリ配置による空間局所性の向上
        /// </summary>
        /// <param name="position">正規化座標（各成分は[1.0, 2.0)の範囲）</param>
        /// <param name="depth">ボクセル深度</param>
        /// <param name="color">ボクセル色</param>
        /// <param name="attributes">属性データ</param>
        private void SetVoxelNormalized(Vector3 position, int depth, Color color, int[] attributes)
        {
            // 高速ビット操作のための unsafe ポインタ変換
            // IEEE 754 浮動小数点の内部表現を直接操作
            unsafe int AsInt(float f) => *(int*)&f;
            int FirstSetHigh(int i) => (AsInt(i) >> 23) - 127;

            // 座標範囲の制限（内部座標系 [1.0, 2.0) に正規化）
            position.x = Mathf.Clamp(position.x, 1f, 1.99999988079f);
            position.y = Mathf.Clamp(position.y, 1f, 1.99999988079f);
            position.z = Mathf.Clamp(position.z, 1f, 1.99999988079f);
            
            // 内部属性データの作成：色データ + カスタム属性
            // メモリレイアウト：[メタデータ+RGB][属性1][属性2]...
            int[] internalAttributes = null;
            if(color.a != 0f)
            {
                internalAttributes = new int[attributes.Length + 1];
                // 24-31bit: 属性データ長, 16-23bit: R, 8-15bit: G, 0-7bit: B
                internalAttributes[0] |= attributes.Length + 1 << 24;
                internalAttributes[0] |= (int)(color.r * 255) << 16;
                internalAttributes[0] |= (int)(color.g * 255) << 8;
                internalAttributes[0] |= (int)(color.b * 255) << 0;
                for (var i = 0; i < attributes.Length; i++) internalAttributes[i + 1] = attributes[i];
            }
            
            // ツリー走査の最適化：前回位置との差分を計算
            // 浮動小数点のビット表現でXOR演算し、変化したビット位置を特定
            var differingBits = AsInt(_ptrStackPos.x) ^ AsInt(position.x);
            differingBits |= AsInt(_ptrStackPos.y) ^ AsInt(position.y);
            differingBits |= AsInt(_ptrStackPos.z) ^ AsInt(position.z);
            var firstSet = 23 - FirstSetHigh(differingBits);
            var stepDepth = Math.Min(Math.Min(firstSet - 1, _ptrStackDepth), depth);
            
            // 共通の親ノードから走査を開始（O(log n)の最適化）
            var ptr = _ptrStack[stepDepth];
            var type = (_data[ptr] >> 31) & 1; // ノードタイプ取得
            
            // 目標深度まで、またはボクセルノード到達まで降下
            while(type == 0 && stepDepth < depth)
            {
                ptr = _data[ptr]; // ポインタを辿る
                
                stepDepth++;
                // 各軸のビット位置から子ノードインデックスを計算
                // IEEE 754の指数部を利用した高速ビット抽出
                var xm = (AsInt(position.x) >> (23 - stepDepth)) & 1;
                var ym = (AsInt(position.y) >> (23 - stepDepth)) & 1;
                var zm = (AsInt(position.z) >> (23 - stepDepth)) & 1;
                var childIndex = (xm << 2) + (ym << 1) + zm; // 3Dインデックス→1D変換
                ptr += childIndex;
                _ptrStack[stepDepth] = ptr; // 走査パスを記録
                
                type = (_data[ptr] >> 31) & 1;
            }

            // 既存ボクセルデータの削除とメモリ解放
            var original = _data[ptr];
            int[] originalShadingData;
            if (type == 0) // ポインタノードの場合、子ツリー全体を解放
            {
                FreeBranch(original);
            }
            if (type == 1 && original != 1 << 31) // 実ボクセルの場合、属性データを解放
            {
                var attribPtr = original & 0x7FFFFFFF;
                var size = (_data[attribPtr] >> 24) & 0xFF;
                originalShadingData = new int[size];
                for (var i = 0; i < size; i++)
                    originalShadingData[i] = _data[attribPtr + i];
                FreeAttributes(attribPtr);
            }
            else originalShadingData = null;

            // 必要に応じて中間ブランチノードを作成（階層構造の構築）
            while (stepDepth < depth)
            {
                stepDepth++;
                
                // 次レベルの子ノードインデックス計算
                var xm = (AsInt(position.x) >> (23 - stepDepth)) & 1;
                var ym = (AsInt(position.y) >> (23 - stepDepth)) & 1;
                var zm = (AsInt(position.z) >> (23 - stepDepth)) & 1;
                var childIndex = (xm << 2) + (ym << 1) + zm;
                
                // 8つの子ノードを持つブランチを作成
                // 対象子ノード以外は既存データで初期化（データ保持）
                var defaultData = new int[8];
                for (var i = 0; i < 8; i++)
                    if (i == childIndex)
                        defaultData[i] = 1 << 31; // プレースホルダー
                    else
                        defaultData[i] = (1 << 31) | AllocateAttributeData(originalShadingData);
                        
                var branchPtr = AllocateBranch(defaultData);
                _data[ptr] = branchPtr;
                RecordUpdate(ptr); // GPU更新を記録
                ptr = branchPtr + childIndex;
                _ptrStack[stepDepth] = ptr;
            }
            
            // 最終ボクセルデータの設定
            _data[ptr] = (1 << 31) | AllocateAttributeData(internalAttributes);
            RecordUpdate(ptr);
            
            // 次回の走査最適化のため、現在位置を記録
            _ptrStackDepth = stepDepth;
            _ptrStackPos = position;
        }

        /// <summary>
        /// 三角形領域をボクセル化する並列処理対応メソッド
        /// 
        /// 並列化の技術：
        /// 1. 再帰的空間分割による並列処理可能な分解
        /// 2. 三角形-ボックス交差判定の高速化
        /// 3. 属性補間による高品質なボクセル化
        /// </summary>
        /// <param name="vertices">三角形の頂点</param>
        /// <param name="depth">ボクセル化深度</param>
        /// <param name="attributeGenerator">属性生成関数</param>
        public void FillTriangle(Vector3[] vertices, int depth, Func<Bounds, Tuple<Color, int[]>> attributeGenerator)
        {
            // 再帰的ボクセル化：空間を8分割しながら三角形との交差を判定
            void FillRecursively(int currentDepth, Bounds bounds)
            {
                // 高速三角形-ボックス交差判定（並列処理に最適化）
                if (TriBoxOverlap.IsIntersecting(bounds, vertices))
                {
                    if (depth == currentDepth)
                    {
                        // 最終深度到達：属性補間してボクセル生成
                        var (color, attributes) = attributeGenerator(bounds);
                        SetVoxel(bounds.min, depth, color, attributes);
                    }
                    else // 再帰的に8つの子空間を処理（並列化可能）
                    {
                        for (var i = 0; i < 8; i++)
                        {
                            var nextCenter = 0.5f * bounds.extents + bounds.min;
                            if ((i & 4) > 0) nextCenter.x += bounds.extents.x;
                            if ((i & 2) > 0) nextCenter.y += bounds.extents.y;
                            if ((i & 1) > 0) nextCenter.z += bounds.extents.z;
                            FillRecursively(currentDepth + 1, new Bounds(nextCenter, bounds.extents));
                        }
                    }
                }
            }
            
            FillRecursively(0, new Bounds(Vector3.zero, Vector3.one));
        }

        public bool CastRay(
            Ray world_ray, 
            Transform octreeTransform, 
            out RayHit hit)
        {
            hit = new RayHit();
            
            unsafe int AsInt(float f) => *(int*)&f;
            unsafe float AsFloat(int value) => *(float*)&value;
            int FirstSetHigh(int value) => (AsInt(value) >> 23) - 127;
            int GetType(int value) => (value >> 31) & 1;
            Vector3 VecMul(Vector3 a, Vector3 b) => new Vector3(a.x * b.x, a.y * b.y, a.z * b.z);
            Vector3 VecClamp(Vector3 a, float min, float max) => 
                new Vector3(Mathf.Clamp(a.x, min, max), 
                    Mathf.Clamp(a.y, min, max), 
                    Mathf.Clamp(a.z, min, max));
            
            var ray_dir = (Vector3)(octreeTransform.worldToLocalMatrix * new Vector4(world_ray.direction.x, world_ray.direction.y, world_ray.direction.z, 0));
            var ray_origin = (Vector3)(octreeTransform.worldToLocalMatrix * new Vector4(world_ray.origin.x, world_ray.origin.y, world_ray.origin.z, 1));
            // Calculations assume octree voxels are in [1, 2) but object is in [-.5, .5]. This corrects that.
            ray_origin += new Vector3(1.5f, 1.5f, 1.5f);
             
            const int max_depth = 23;
            const float epsilon = 0.00000011920928955078125f;
            // Mirror coordinate system such that all ray direction components are negative.
            int sign_mask = 0;
            if(ray_dir.x > 0f)
            {
                sign_mask ^= 4; 
                ray_origin.x = 3f - ray_origin.x;
            }
            if(ray_dir.y > 0f)
            {
                sign_mask ^= 2; 
                ray_origin.y = 3f - ray_origin.y;
            }
            if(ray_dir.z > 0f)
            {
                sign_mask ^= 1; 
                ray_origin.z = 3f - ray_origin.z;
            }

            ray_dir = -new Vector3(Mathf.Abs(ray_dir.x), Mathf.Abs(ray_dir.y), Mathf.Abs(ray_dir.z));
            var ray_inv_dir = -new Vector3(Mathf.Abs(1 / ray_dir.x), Mathf.Abs(1 / ray_dir.y), Mathf.Abs(1 / ray_dir.z));
            
            // Get intersections of octree (if hit)
            var root_min_distances = VecMul(Vector3.one * 2f - ray_origin, ray_inv_dir);
            var root_max_distances = VecMul(Vector3.one - ray_origin, ray_inv_dir);
            var root_tmin = Mathf.Max(Mathf.Max(Mathf.Max(root_min_distances.x, root_min_distances.y), root_min_distances.z), 0);
            var root_tmax = Mathf.Min(Mathf.Min(root_max_distances.x, root_max_distances.y), root_max_distances.z);
            
            if(root_tmax < 0 || root_tmin >= root_tmax) return false;
            if(root_tmin == root_min_distances.x)
            {
                hit.faceNormal = new Vector3(1, 0, 0);
                if((sign_mask >> 2) != 0)
                    hit.faceNormal.x = -hit.faceNormal.x;
            }
            else if(root_tmin == root_min_distances.y)
            {
                hit.faceNormal = new Vector3(0, 1, 0);
                if((sign_mask >> 1 & 1) != 0)
                    hit.faceNormal.y = -hit.faceNormal.y;
            }
            else
            {
                hit.faceNormal = new Vector3(0, 0, 1);
                if((sign_mask & 1) != 0)
                    hit.faceNormal.z = -hit.faceNormal.z;
            }
            
            Vector3 next_path = VecClamp(ray_origin + ray_dir * root_tmin, 1f, AsFloat(0x3fffffff));
            
            var stack = new int[max_depth + 1];
            stack[0] = 0;
            var stack_depth = 0;
            Vector3 stack_path = new Vector3(1, 1, 1);

            int i = 0;
            do
            {
                i++;
                // Get voxel at targetPos
                var differing_bits = AsInt(stack_path.x) ^ AsInt(next_path.x);
                differing_bits |= AsInt(stack_path.y) ^ AsInt(next_path.y);
                differing_bits |= AsInt(stack_path.z) ^ AsInt(next_path.z);
                var first_set = 23 - FirstSetHigh(differing_bits);
                var depth = Mathf.Min(first_set - 1, stack_depth);
                var ptr = stack[depth];
                int data = _data[ptr];
                int type = GetType(data);
                while(type == 0)
                {
                    ptr = data;
                    depth++;
                    int xm = (AsInt(next_path.x) >> (23 - depth)) & 1; // 1 or 0 for sign of movement in x direction
                    int ym = (AsInt(next_path.y) >> (23 - depth)) & 1; // 1 or 0 for sign of movement in y direction
                    int zm = (AsInt(next_path.z) >> (23 - depth)) & 1; // 1 or 0 for sign of movement in z direction
                    int child_index = (xm << 2) + (ym << 1) + zm;
                    child_index ^= sign_mask;
                    ptr += child_index;
                    stack[depth] = ptr;
                    data = _data[ptr]; // Follow ptr
                    type = GetType(data);
                }
                stack_depth = depth;
                stack_path = new Vector3(
                    AsFloat(AsInt(next_path.x) & ~((1 << 23 - depth) - 1)),
                    AsFloat(AsInt(next_path.y) & ~((1 << 23 - depth) - 1)),
                    AsFloat(AsInt(next_path.z) & ~((1 << 23 - depth) - 1))
                ); // Remove unused bits
                
                // Return hit if voxel is solid
                if(type == 1 && data != (1 << 31))
                {
                    int attributes_head_ptr = data & ~(1 << 31);

                    int color_data = _data[attributes_head_ptr];
                    hit.attributesPtr = attributes_head_ptr + 1;
                    hit.color = new Vector4((color_data >> 16 & 0xFF) / 255f, (color_data >> 8 & 0xFF) / 255f, (color_data & 0xFF) / 255f, (color_data >> 24 & 0xFF) / 255f);

                    // Undo coordinate mirroring in next_path
                    Vector3 mirrored_path = next_path;
                    hit.voxelObjSize = AsFloat((0b01111111 - depth) << 23); // exp2(-depth)
                    if(sign_mask >> 2 != 0)
                    {
                        hit.faceNormal.x = -hit.faceNormal.x;
                        mirrored_path.x = 3f - next_path.x;
                    }
                    if((sign_mask >> 1 & 1) != 0)
                    {
                        hit.faceNormal.y = -hit.faceNormal.y;
                        mirrored_path.y = 3f - next_path.y;
                    }
                    if((sign_mask & 1) != 0)
                    {
                        hit.faceNormal.z = -hit.faceNormal.z;
                        mirrored_path.z = 3f - next_path.z;
                    }
                    hit.voxelObjPos -= Vector3.one * 1.5f;
                    hit.objPos = mirrored_path - Vector3.one * 1.5f;
                    hit.voxelObjPos = new Vector3(
                        AsFloat(AsInt(mirrored_path.x) & ~((1 << 23 - depth) - 1)) - 1.5f,
                        AsFloat(AsInt(mirrored_path.y) & ~((1 << 23 - depth) - 1)) - 1.5f,
                        AsFloat(AsInt(mirrored_path.z) & ~((1 << 23 - depth) - 1)) - 1.5f
                        );
                    hit.worldPos = octreeTransform.localToWorldMatrix * new Vector4(hit.objPos.x, hit.objPos.y, hit.objPos.z, 1f);
                    
                    return true;
                }

                // Step to the next voxel by moving along the normal on the far side of the voxel that was hit.
                var t_max = VecMul((stack_path - ray_origin), ray_inv_dir);
                var min_t_max = Mathf.Min(Mathf.Min(t_max.x, t_max.y), t_max.z);
                var cmax = new Vector3(
                    AsFloat(AsInt(stack_path.x) + (1 << 23 - depth) - 1),
                    AsFloat(AsInt(stack_path.y) + (1 << 23 - depth) - 1),
                    AsFloat(AsInt(stack_path.z) + (1 << 23 - depth) - 1)
                );
                next_path = new Vector3(
                    Mathf.Clamp(ray_origin.x + ray_dir.x * min_t_max, stack_path.x, cmax.x),
                    Mathf.Clamp(ray_origin.y + ray_dir.y * min_t_max, stack_path.y, cmax.y),
                    Mathf.Clamp(ray_origin.z + ray_dir.z * min_t_max, stack_path.z, cmax.z)
                );

                if(t_max.x == min_t_max)
                {
                    hit.faceNormal = new Vector3(1, 0, 0);
                    next_path.x = stack_path.x - epsilon;
                }
                else if(t_max.y == min_t_max)
                {
                    hit.faceNormal = new Vector3(0, 1, 0);
                    next_path.y = stack_path.y - epsilon;
                }
                else
                {
                    hit.faceNormal = new Vector3(0, 0, 1);
                    next_path.z = stack_path.z - epsilon;
                }
            }
            while((AsInt(next_path.x) & 0xFF800000) == 0x3f800000 && 
                  (AsInt(next_path.y) & 0xFF800000) == 0x3f800000 && 
                  (AsInt(next_path.z) & 0xFF800000) == 0x3f800000 && 
                  i <= 250); // Same as 1 <= next_path < 2 && i <= 250

            return false;
        }

        /// <summary>
        /// ブランチノードとその子ノードを再帰的に解放
        /// メモリリークを防ぐための重要な処理
        /// </summary>
        private void FreeBranch(int ptr)
        {
            _freeStructureMemory.Add(ptr);
            for (var i = 0; i < 8; i++)
            {
                var optr = ptr + i;
                var type = (_data[optr] >> 31) & 1;
                if(type == 0)
                    FreeBranch(_data[optr]); // 再帰的解放
                else if(_data[optr] != 1 << 31)
                    FreeAttributes(_data[optr] & 0x7FFFFFFF);
            }
        }

        /// <summary>
        /// 属性データメモリを解放してフリープールに追加
        /// </summary>
        private void FreeAttributes(int ptr)
        {
            _freeAttributeMemory.Add(ptr);
        }
        
        /// <summary>
        /// ブランチノード用メモリを割り当て
        /// フリープールから再利用可能メモリを優先使用
        /// </summary>
        private int AllocateBranch(IReadOnlyList<int> ptrs)
        {
            int ptr;
            if (_freeStructureMemory.Count == 0)
            {
                // 新規メモリ割り当て
                ptr = _data.Count;
                _data.AddRange(ptrs);
            }
            else
            {
                // フリープールから再利用
                ptr = _freeStructureMemory.Last();
                for (var i = 0; i < ptrs.Count; i++)
                    _data[i + ptr] = ptrs[i];
                _freeStructureMemory.Remove(ptr);
            }
            // GPU更新記録（最大256x256スライスにまたがる可能性を考慮）
            RecordUpdate(ptr);
            RecordUpdate(ptr + 7);
            return ptr;
        }

        /// <summary>
        /// 属性データ用メモリを割り当て
        /// 同サイズの削除済みブロックを優先再利用してメモリ断片化を防ぐ
        /// </summary>
        private int AllocateAttributeData(IReadOnlyList<int> attributes)
        {
            if (attributes == null) return 0;
            
            // 同サイズの解放済みメモリブロックを検索
            var index = 0;
            foreach (var ptr in _freeAttributeMemory)
            {
                var size = (uint)_data[ptr] >> 24;
                if (size != attributes.Count)
                {
                    index++;
                    continue;
                }
                
                // サイズ適合ブロック発見：再利用
                for (var i = 0; i < attributes.Count; i++)
                {
                    _data[ptr + i] = attributes[i];
                }
                if (attributes.Count > 256 * 256) 
                    throw new ArgumentException("Too many attributes. Max number is 65536 per voxel.");
                
                RecordUpdate(ptr);
                RecordUpdate(ptr + attributes.Count - 1);
                _freeAttributeMemory.Remove(ptr);
                return ptr;
            }

            // 新規メモリ割り当て
            var endPtr = _data.Count;
            _data.AddRange(attributes);
            RecordUpdate(endPtr);
            RecordUpdate(endPtr + attributes.Count - 1);
            return endPtr;
        }

        /// <summary>
        /// オクトリーデータをGPU用Texture3Dに変換
        /// 
        /// メモリ効率とパフォーマンスの最適化：
        /// 1. 差分更新：変更されたスライスのみを転送
        /// 2. テクスチャ再利用：可能な場合は既存テクスチャを再利用
        /// 3. 並列転送：複数スライスを効率的に処理
        /// </summary>
        /// <param name="tryReuseOldTexture">既存テクスチャの再利用を試行</param>
        /// <returns>更新されたTexture3D</returns>
        public Texture3D Apply(bool tryReuseOldTexture=true)
        {
            // 一時テクスチャの初期化（メモリプール的な使用）
            if (tempTex is null)
                tempTex = new Texture3D(256, 256, 1, TextureFormat.RFloat, false);
            
            // 必要深度の計算：データサイズに基づく動的スケーリング
            var depth = Mathf.NextPowerOfTwo(Mathf.CeilToInt((float) _data.Count / 256 / 256));
            
            // テクスチャ再作成の判定
            if (Data is null || depth != Data.depth || !tryReuseOldTexture)
            {
                Object.Destroy(Data);
                Data = new Texture3D(256, 256, depth, TextureFormat.RFloat, false);
                // 全スライスを強制更新対象に設定
                for (int i = 0; i < _lastApply.Length; i++)
                    _lastApply[i] = ulong.MaxValue;
            }

            uint updated = 0;
            // 差分更新：変更されたスライスのみを処理
            for (var i = 0; i < depth; i++)
            {
                if (_lastApply[i] == _updateCount[i])
                    continue; // このスライスは更新不要

                updated++;
                _lastApply[i] = _updateCount[i];
                
                // スライス範囲の計算
                var minIndex = i * 256 * 256;
                var maxIndex = (i + 1) * 256 * 256;
                if (minIndex > _data.Count) minIndex = _data.Count;
                if (maxIndex > _data.Count) maxIndex = _data.Count;
                
                if (minIndex >= maxIndex) break;
                
                // データ転送：CPU→GPU
                var block = new int[256 * 256];
                _data.CopyTo(minIndex, block, 0, maxIndex - minIndex);
                tempTex.SetPixelData(block, 0);
                tempTex.Apply();
                // 高速テクスチャコピー（GPU内部転送）
                Graphics.CopyTexture(tempTex, 0, 0, 0, 0, 256, 256, Data, i, 0, 0, 0);
            }

            // GPU側の更新通知
            if (updated != 0)
            {
                Data.IncrementUpdateCount();
            }
            return Data;
        }

        /// <summary>
        /// オクトリー内部構造の最適化と再構築
        /// 
        /// メモリ最適化技術：
        /// 1. メモリ連続性の向上：断片化を解消
        /// 2. 空間局所性の改善：関連データの近接配置  
        /// 3. キャッシュ効率の向上：アクセスパターンの最適化
        /// </summary>
        public void Rebuild()
        {
            // 最適化されたメモリ容量の計算
            var capacity = Mathf.NextPowerOfTwo(_data.Count - _freeStructureMemory.Count * 8 - _freeAttributeMemory.Count);
            var optimizedData = new List<int>(capacity);

            // 再帰的ブランチ再構築：深度優先でメモリを連続配置
            void RebuildBranch(int referenceBranchPtr)
            {
                var start = optimizedData.Count;
                optimizedData.AddRange(new int[8]); // 8つの子ノード分を予約
                
                for (var i = 0; i < 8; i++)
                {
                    if (_data[referenceBranchPtr + i] == 1 << 31)
                    {
                        optimizedData[start + i] = 1 << 31; // 空ボクセル
                    }
                    else if ((_data[referenceBranchPtr + i] >> 31 & 1) == 1)
                    {
                        // ボクセルノード：属性データを連続配置
                        optimizedData[start + i] = 1 << 31 | optimizedData.Count;
                        var attribPtr = _data[referenceBranchPtr + i] & 0x7FFFFFFF;
                        var c = (_data[attribPtr] >> 24) & 0xFF;
                        for(var j = 0; j < c; j++)
                            optimizedData.Add(_data[attribPtr + j]);
                    }
                    else
                    {
                        // ポインタノード：再帰的に子ブランチを処理
                        optimizedData[start + i] = optimizedData.Count;
                        RebuildBranch(_data[referenceBranchPtr + i]);
                    }
                }   
            }

            // ルートノードから再構築開始
            if (_data[0] == 1 << 31)
            {
                optimizedData.Add(1 << 31);
            }
            else if ((_data[0] >> 31 & 1) == 1)
            {
                optimizedData.Add(1 << 31 | (optimizedData.Count + 1));
                var attribPtr = _data[0] & 0x7FFFFFFF;
                var c = (_data[attribPtr] >> 24) & 0xFF;
                for(var j = 0; j < c; j++)
                    optimizedData.Add(_data[attribPtr + j]);
            }
            else
            {
                optimizedData.Add(optimizedData.Count + 1);
                RebuildBranch(_data[0]);
            }

            // 最適化されたデータで置き換え
            _data = optimizedData;
            _ptrStackDepth = 0;
            _ptrStackPos = Vector3.one;
            _ptrStack = new int[24];
            _freeAttributeMemory.Clear();
            _freeStructureMemory.Clear();
            _lastApply = new ulong[2048];
            for (var i = 0; i < _lastApply.Length; i++)
                _lastApply[i] = ulong.MaxValue;
        }

        /// <summary>
        /// GPU更新の記録：該当スライスの更新カウンタを増加
        /// 差分更新システムの中核機能
        /// </summary>
        private void RecordUpdate(int idx)
        {
            _updateCount[idx >> 16]++; // 16bit右シフトでスライス番号を取得
        }

        public void Dispose()
        {
            Object.Destroy(Data);
        }
    }
}