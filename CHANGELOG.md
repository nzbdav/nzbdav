# Changelog

## [0.6.9](https://github.com/nzbdav/nzbdav/compare/v0.6.8...v0.6.9) (2026-07-10)


### Features

* modernize console logging ([ad3f3ba](https://github.com/nzbdav/nzbdav/commit/ad3f3baebc2fab5b945429256323c5e8be90ad7e))
* **ui:** add tailwind design foundation ([2a4f4f4](https://github.com/nzbdav/nzbdav/commit/2a4f4f44a5c15e84a50eddb61b9a9cea4e977b80))
* **ui:** restyle application shell ([d91d211](https://github.com/nzbdav/nzbdav/commit/d91d211d4373dd2c98c94b712010f4cf40a32fd1))
* **ui:** restyle authentication screens ([7ad644b](https://github.com/nzbdav/nzbdav/commit/7ad644b43d3339d150a9b22a9de25ab40db6339f))
* **ui:** restyle explore and health views ([bb7a07c](https://github.com/nzbdav/nzbdav/commit/bb7a07c0c33f2c49661bbc437ddee7dc5f5cd3ef))
* **ui:** restyle queue interface ([b924989](https://github.com/nzbdav/nzbdav/commit/b92498957606d8849098a1d66eb0cbf741c6b806))
* **ui:** restyle settings interface ([cdac26e](https://github.com/nzbdav/nzbdav/commit/cdac26e332690573742bd7f5cc3e73dafc327a9f))


### Bug Fixes

* **ci:** publish only v-prefixed release tags ([55329a6](https://github.com/nzbdav/nzbdav/commit/55329a6c76cb3a09c2dc4268fdc48571bfd6fcfc))
* **deps:** Bump @types/node from 25.9.5 to 26.1.0 in /frontend ([#24](https://github.com/nzbdav/nzbdav/issues/24)) ([e9d7620](https://github.com/nzbdav/nzbdav/commit/e9d7620e0fbca372fd56aaab1db469a206b9dd69))
* **deps:** Bump http-proxy-middleware from 3.0.7 to 4.1.1 in /frontend ([#26](https://github.com/nzbdav/nzbdav/issues/26)) ([849bbf4](https://github.com/nzbdav/nzbdav/commit/849bbf4247327812d1540596bcdf6dfdc268c073))
* **deps:** bump SharpCompress from 0.39.0 to 0.49.1 ([f0a1788](https://github.com/nzbdav/nzbdav/commit/f0a1788a946ff1599c160de522c2cb58fed893e3))
* **deps:** Bump typescript from 5.9.3 to 6.0.3 in /frontend ([#20](https://github.com/nzbdav/nzbdav/issues/20)) ([2584aa9](https://github.com/nzbdav/nzbdav/commit/2584aa961f0595bedd8d668715620fe4057ccc9e))
* **ui:** handle unavailable WebDAV directories ([ae29142](https://github.com/nzbdav/nzbdav/commit/ae2914211fda737cae065af4f1e378844f0f6f43))

## [0.6.8](https://github.com/nzbdav/nzbdav/compare/v0.6.7...v0.6.8) (2026-07-10)


### Features

* **usenet:** consume chunked decoded-body API ([d0996d3](https://github.com/nzbdav/nzbdav/commit/d0996d31822c15752144aff2f306f360c60e21a2))
* **usenet:** keep paused streaming connections warm longer ([fe7a779](https://github.com/nzbdav/nzbdav/commit/fe7a7790624bd1e7f8a7384b22062e8199035b5b))
* **usenet:** pipeline segment BODY requests ([b911d06](https://github.com/nzbdav/nzbdav/commit/b911d06e17d4199a1f9b1c8fcfa23cf0dedc7f2a))
* **webdav:** persist segment ranges for arithmetic seeks ([c22650e](https://github.com/nzbdav/nzbdav/commit/c22650ed03c81f1dc1ead42c15ecd1c5cb58c6f9))


### Bug Fixes

* **api:** mask stored credentials in config responses ([8dfedb2](https://github.com/nzbdav/nzbdav/commit/8dfedb28a6a1c727946522267019c0088ad61c5f))
* **api:** stop returning raw exception messages ([0e2accd](https://github.com/nzbdav/nzbdav/commit/0e2accd3da421d30347be6f434d4fcc248e5dcab))
* **arr:** contain monitoring loop failures ([9d7aed2](https://github.com/nzbdav/nzbdav/commit/9d7aed25de87176b87166638440242e476d031bc))
* **arr:** return parent directories in root-first order ([e76531b](https://github.com/nzbdav/nzbdav/commit/e76531bca4ebc639c5c8de67a28e07300a3aa23e))
* **auth:** cache successful WebDAV password checks ([ba60894](https://github.com/nzbdav/nzbdav/commit/ba608945aabc8c77172442476ddfcad7bbe4a6f8))
* **auth:** compare API and download keys in constant time ([a6541e4](https://github.com/nzbdav/nzbdav/commit/a6541e4cbff9e832e8d1010fb848209328a057f6))
* **auth:** preserve sessions across development reloads ([d293df1](https://github.com/nzbdav/nzbdav/commit/d293df1b995a41c10584a5f22abcf3a8bc592f87))
* **backend:** enable server garbage collection ([1169b00](https://github.com/nzbdav/nzbdav/commit/1169b007723c61cdfd2703e431c94be0e9d0a119))
* **backend:** respect configured logging level ([afc72d3](https://github.com/nzbdav/nzbdav/commit/afc72d3d686a6d98a688d4c68f7be4d269098999))
* **ci:** deploy Docker images when release-please creates a release ([01eb27a](https://github.com/nzbdav/nzbdav/commit/01eb27ad61b66c292ce9a0df8c1fcca85122d367))
* **ci:** drop unsupported configfile test flag ([943863c](https://github.com/nzbdav/nzbdav/commit/943863c69275a6dc8d2b656241929cdf5b95adc0))
* **config:** cache parsed structured settings ([1a5e70b](https://github.com/nzbdav/nzbdav/commit/1a5e70bbf881f449e701bfd6d2ca5f8fc1836b8a))
* **db:** cache deserialized streaming metadata ([3eec4c0](https://github.com/nzbdav/nzbdav/commit/3eec4c05e0b79ef3a3ea291b5e0d2e63a368552a))
* **db:** disable tracking for read-only queries ([1d70198](https://github.com/nzbdav/nzbdav/commit/1d70198815ab6516b4f07b5896cf79bf21bb0c34))
* **db:** enable WAL and busy timeout ([56cac83](https://github.com/nzbdav/nzbdav/commit/56cac83a005b666f934645c7835f1711ea958960))
* **deps:** bump react-router packages to 8.1.0 ([a7fb1d2](https://github.com/nzbdav/nzbdav/commit/a7fb1d2e186302f0b4003aec971658793cc2689c))
* **deps:** bump UsenetSharp to 1.2.2 ([3c24411](https://github.com/nzbdav/nzbdav/commit/3c2441120823a71c2e457062929967b4956235e3))
* **docker:** propagate child process exit codes ([be67ea8](https://github.com/nzbdav/nzbdav/commit/be67ea8974a2ff27365c4283c54fdd120732801c))
* harden security-sensitive request handling ([1e0f2dc](https://github.com/nzbdav/nzbdav/commit/1e0f2dc3ee637d8603c94da7271e1932922fd1e2))
* **health:** bound the missing segment cache ([cdbc614](https://github.com/nzbdav/nzbdav/commit/cdbc61481f202dcaf133a915ce3a14f1962d684d))
* **nntp:** close pipelined transfer lifecycle gaps ([27ccc31](https://github.com/nzbdav/nzbdav/commit/27ccc31abdc818ae95b46cc9ba326ea8e6a3c6dd))
* **queue:** start queue processing at startup ([3b6904f](https://github.com/nzbdav/nzbdav/commit/3b6904f3e51fb9be7babc5bc3ef454746b572ac9))
* **sab:** guard addurl fetches against SSRF ([6249dc4](https://github.com/nzbdav/nzbdav/commit/6249dc47152a56478cceba252684d9f8e7d56567))
* **sab:** make history delete idempotent for missing ids ([#36](https://github.com/nzbdav/nzbdav/issues/36)) ([e16b7c5](https://github.com/nzbdav/nzbdav/commit/e16b7c543e6c8c7444965798c3a693001167a5bb))
* **sab:** retry history delete save when rows vanish concurrently ([1b9bf1e](https://github.com/nzbdav/nzbdav/commit/1b9bf1e28162ccf13809474de8bd649411f5c584))
* **sab:** validate category before backup paths ([f56812a](https://github.com/nzbdav/nzbdav/commit/f56812a1dc04984ba75b261fb6b991e321da6675))
* **ui:** guard backend websocket parsing ([7e65ff9](https://github.com/nzbdav/nzbdav/commit/7e65ff90462f810439effe4fca4c2c068bd34d1b))
* **ui:** improve standalone frontend status ([b47ebde](https://github.com/nzbdav/nzbdav/commit/b47ebdef341c3f721bd1711d14d7c34f6f267d3e))
* **ui:** replace incompatible queue dropzone ([6b5dc08](https://github.com/nzbdav/nzbdav/commit/6b5dc08721a3b5f4518edaeff6470674a7c09369))
* **usenet:** skip websocket encoding without subscribers ([e837451](https://github.com/nzbdav/nzbdav/commit/e837451b6492dc19db909f541efa4a16334043c7))
* **webdav:** decrypt AES streams in 256 KiB runs ([6b4f867](https://github.com/nzbdav/nzbdav/commit/6b4f867664a7dc54298c39b269a923f552f23577))
* **webdav:** discard seek prefixes in 64 KiB chunks ([c649dea](https://github.com/nzbdav/nzbdav/commit/c649dea5720ad009771eb5455b388f7fd4532271))
* **webdav:** enforce seek and stream lifecycle contracts ([c390134](https://github.com/nzbdav/nzbdav/commit/c390134875dee5d285979cbacfc0283c29a8218d))
* **webdav:** honor construction cancellation token in CancellableStream reads ([452227b](https://github.com/nzbdav/nzbdav/commit/452227bb6c63fac6683c22d9346baf787b197ede))
* **webdav:** pool the response copy buffer ([5997260](https://github.com/nzbdav/nzbdav/commit/5997260c8abffd8f334b3dcd79d02ff67ce71895))
* **webdav:** preserve prefetch on small forward seeks ([79f14a4](https://github.com/nzbdav/nzbdav/commit/79f14a41ea00337bfa6557d49d7942331046bf0b))
* **webdav:** serve correct suffix byte ranges ([34a5530](https://github.com/nzbdav/nzbdav/commit/34a55302267ba1cee6ba82ec0d88dd564bfbaa89))
* **webdav:** surface segment producer failures ([9610dc5](https://github.com/nzbdav/nzbdav/commit/9610dc517363dedf37372cae994fd516d7b2ae59))
* **webdav:** throw when stream reads are cancelled ([5e4cb9a](https://github.com/nzbdav/nzbdav/commit/5e4cb9aedcdc86ac0a4e1d76d604f44a457a6519))

## [0.6.7](https://github.com/nzbdav/nzbdav/compare/v0.6.6...v0.6.7) (2026-07-10)


### Bug Fixes

* **ci:** quote pre-release job if for Dependabot parser ([c0f3f48](https://github.com/nzbdav/nzbdav/commit/c0f3f4800eac8b31868987df84a3ce5374c72673))
* **deps:** align react-router packages and group Dependabot updates ([71d4490](https://github.com/nzbdav/nzbdav/commit/71d4490286b2c8245bc774cc1024c36a4e083c23))
* **deps:** bump the github-actions group with 3 updates ([#33](https://github.com/nzbdav/nzbdav/issues/33)) ([f4ce334](https://github.com/nzbdav/nzbdav/commit/f4ce334cb59fd56c9d0f4edb503d48e33215ff2c))
* **deps:** Bump the nuget-minor-and-patch group with 3 updates ([#34](https://github.com/nzbdav/nzbdav/issues/34)) ([4f3864c](https://github.com/nzbdav/nzbdav/commit/4f3864c416fa9e0992d781c96bad9a1d750b27c3))
* **deps:** configure Dependabot auth for UsenetSharp NuGet feed ([0b676c2](https://github.com/nzbdav/nzbdav/commit/0b676c22723e2d4aacf6cc03090425c7c85167da))
* **docker:** publish dev tag from pre-release workflow ([409c6ee](https://github.com/nzbdav/nzbdav/commit/409c6ee12dbbfddd6df80ba4156c6e91c246bbd6))
* **docker:** use vMAJOR and vMAJOR.MINOR rolling tags ([9fd3fb6](https://github.com/nzbdav/nzbdav/commit/9fd3fb63db3c7160d5889e081d957b102b0aa1c1))

## [0.6.6](https://github.com/nzbdav/nzbdav/compare/v0.6.5...v0.6.6) (2026-07-10)


### Bug Fixes

* **ci:** add self-managed dependency submission with NuGet auth ([b35b856](https://github.com/nzbdav/nzbdav/commit/b35b856f58685ebb1eda9cfb16f15ab404d6d2b8))
* **ci:** deploy Docker images on release published event ([c9f8207](https://github.com/nzbdav/nzbdav/commit/c9f8207c95abb41f6ab413a85d0d755dcc3d3f07))
* **ci:** quote pre-release image tag for Dependabot parser ([0066a1f](https://github.com/nzbdav/nzbdav/commit/0066a1f07db64de69c7c8044fdd80b64b84f3169))
* **deps:** remove invalid GITHUB_TOKEN from Dependabot registry config ([d5bb833](https://github.com/nzbdav/nzbdav/commit/d5bb833b23683858c211e230375d3f1f415e540e))
* **ui:** emit node build output to dist-node only ([507bb66](https://github.com/nzbdav/nzbdav/commit/507bb66b60c0d810b3570133620a6a39e4397643))

## [0.6.5](https://github.com/hoivikaj/nzbdav/compare/v0.6.4...v0.6.5) (2026-07-10)


### Features

* add backend support for scheduling the RemoveOrphanedFiles task. ([ffcdcfd](https://github.com/hoivikaj/nzbdav/commit/ffcdcfd4e25d1ced7648bef29e0a3d246c419410))
* add NZB backup settings to frontend. ([55260d4](https://github.com/hoivikaj/nzbdav/commit/55260d41d00722b3881b4eeea5d5d07e86d5704b))
* add ui setting to schedule the RemoveOrphanedFiles task. ([807573b](https://github.com/hoivikaj/nzbdav/commit/807573bd7411afcb22731e9a40dc0c8f519b2f95))
* allow exporting nzb from history table. ([7928d4b](https://github.com/hoivikaj/nzbdav/commit/7928d4b1fb5fc785828b4a7b211d5c62b37b6243))
* backup incoming nzbs to configured directory when enabled. ([c2b3692](https://github.com/hoivikaj/nzbdav/commit/c2b369229ae7ebd0bd3bfaa14c99f939d93c241e))
* index QueueItems table by category and filename. ([9116bfc](https://github.com/hoivikaj/nzbdav/commit/9116bfc93407dc867206f16f644f7201591ff0e1))
* organize /nzbs webdav dir by category. ([404d418](https://github.com/hoivikaj/nzbdav/commit/404d418a8a0a9d1465c1115b87a8506a5b9d56de))
* support the TZ (timezone) env variable. ([cfe0298](https://github.com/hoivikaj/nzbdav/commit/cfe02980593b07dd7800a2ce42cdfcd1765cdd20))


### Bug Fixes

* Allow special characters for filename-passwords ([#308](https://github.com/hoivikaj/nzbdav/issues/308)) ([df8b845](https://github.com/hoivikaj/nzbdav/commit/df8b84515f7b134485c4a07143413de2c1fe2e40))
* compatability issues with NZBDonkey ([#316](https://github.com/hoivikaj/nzbdav/issues/316)) ([b2d0f2a](https://github.com/hoivikaj/nzbdav/commit/b2d0f2a4c6b48cca688bdffb91ba1b71a3fb1b84))
* **deps:** bump @tailwindcss/vite from 4.1.11 to 4.2.1 in /frontend ([#330](https://github.com/hoivikaj/nzbdav/issues/330)) ([3389627](https://github.com/hoivikaj/nzbdav/commit/3389627c98a50370d580d614ebb0f0874d507219))
* **deps:** bump @types/express-serve-static-core ([#347](https://github.com/hoivikaj/nzbdav/issues/347)) ([95f8953](https://github.com/hoivikaj/nzbdav/commit/95f89533f1ed3f16a4c862f3e67f83d6b6ddf401))
* **deps:** bump @types/node from 20.19.10 to 25.4.0 in /frontend ([#328](https://github.com/hoivikaj/nzbdav/issues/328)) ([7239021](https://github.com/hoivikaj/nzbdav/commit/72390216d65380230fff1b0c091ec677e892a223))
* **deps:** bump @types/node from 25.4.0 to 25.5.0 in /frontend ([#381](https://github.com/hoivikaj/nzbdav/issues/381)) ([680e80d](https://github.com/hoivikaj/nzbdav/commit/680e80df44d4a86a6c896e25c54762159fd69741))
* **deps:** bump actions/checkout from 4 to 6 ([#317](https://github.com/hoivikaj/nzbdav/issues/317)) ([b41042e](https://github.com/hoivikaj/nzbdav/commit/b41042ea66aeb30859674e9885f15143fe8545c7))
* **deps:** bump bootstrap from 5.3.7 to 5.3.8 in /frontend ([#329](https://github.com/hoivikaj/nzbdav/issues/329)) ([1790518](https://github.com/hoivikaj/nzbdav/commit/17905189d379ae0d8ed0e2934d3acde7e3009785))
* **deps:** bump cross-env from 7.0.3 to 10.1.0 in /frontend ([#336](https://github.com/hoivikaj/nzbdav/issues/336)) ([b8d6693](https://github.com/hoivikaj/nzbdav/commit/b8d6693225e819127bb40063f335c8ab7a4f5ca0))
* **deps:** bump docker/login-action from 3 to 4 ([#321](https://github.com/hoivikaj/nzbdav/issues/321)) ([12094ea](https://github.com/hoivikaj/nzbdav/commit/12094ea4e4797799981155ac801d9730ddf824db))
* **deps:** bump express and @types/express in /frontend ([#324](https://github.com/hoivikaj/nzbdav/issues/324)) ([1539ce5](https://github.com/hoivikaj/nzbdav/commit/1539ce5d50ac53f1ca39a65166d17ed80fb295e1))
* **deps:** bump isbot from 5.1.29 to 5.1.35 in /frontend ([#322](https://github.com/hoivikaj/nzbdav/issues/322)) ([2d0d069](https://github.com/hoivikaj/nzbdav/commit/2d0d0694ecc060134810e7c2d4bbb07aaa94a74f))
* **deps:** bump isbot from 5.1.35 to 5.1.36 in /frontend ([#349](https://github.com/hoivikaj/nzbdav/issues/349)) ([0619772](https://github.com/hoivikaj/nzbdav/commit/06197726fd2be0695027e5a7ca1ecf8c55d21586))
* **deps:** bump isbot from 5.1.36 to 5.1.37 in /frontend ([#379](https://github.com/hoivikaj/nzbdav/issues/379)) ([b054f42](https://github.com/hoivikaj/nzbdav/commit/b054f42a8e2b715f94995b5e37763f8c0d9651f7))
* **deps:** Bump Microsoft.AspNetCore.OpenApi from 10.0.1 to 10.0.4 ([#332](https://github.com/hoivikaj/nzbdav/issues/332)) ([7e0cfd6](https://github.com/hoivikaj/nzbdav/commit/7e0cfd6acada37b2b2de8961eae9d095a97f8417))
* **deps:** Bump Microsoft.EntityFrameworkCore.Design from 10.0.1 to 10.0.4 ([#334](https://github.com/hoivikaj/nzbdav/issues/334)) ([88fa597](https://github.com/hoivikaj/nzbdav/commit/88fa5976bda674e98d2bf57802fbddeb721abaaa))
* **deps:** Bump Microsoft.EntityFrameworkCore.Sqlite from 10.0.1 to 10.0.4 ([#338](https://github.com/hoivikaj/nzbdav/issues/338)) ([e19d72c](https://github.com/hoivikaj/nzbdav/commit/e19d72cd42b9ea302fc6e5dae32ea0e2652f1094))
* **deps:** bump mime-types from 3.0.1 to 3.0.2 in /frontend ([#323](https://github.com/hoivikaj/nzbdav/issues/323)) ([8866951](https://github.com/hoivikaj/nzbdav/commit/88669514ff6ff279647cd8f92f23ae9f3aa908a4))
* **deps:** bump react-dropzone from 14.3.8 to 15.0.0 in /frontend ([#348](https://github.com/hoivikaj/nzbdav/issues/348)) ([ab24e15](https://github.com/hoivikaj/nzbdav/commit/ab24e15c3b8ec3cda5c07c2943adbf1fadd1c52c))
* **deps:** bump tailwindcss from 4.1.11 to 4.2.1 in /frontend ([#335](https://github.com/hoivikaj/nzbdav/issues/335)) ([2a62a41](https://github.com/hoivikaj/nzbdav/commit/2a62a41e8b3b094f69bbb687bec775776530435b))
* **deps:** Bump the dotnet group with 3 updates ([#395](https://github.com/hoivikaj/nzbdav/issues/395)) ([aae1e43](https://github.com/hoivikaj/nzbdav/commit/aae1e4367bb70f7a0a517779f453680c1e06c2bb))
* **deps:** bump the github-actions group across 1 directory with 2 updates ([75f7dfb](https://github.com/hoivikaj/nzbdav/commit/75f7dfb16815ed25cc57dcde0c5cb615ae70ebaa))
* **deps:** bump the github-actions group across 1 directory with 2 updates ([2f4d6c3](https://github.com/hoivikaj/nzbdav/commit/2f4d6c3263a9b8f0f5218b0948d79e208e397ff4))
* **deps:** bump the github-actions group with 3 updates ([#350](https://github.com/hoivikaj/nzbdav/issues/350)) ([e017ca9](https://github.com/hoivikaj/nzbdav/commit/e017ca9d868b624b3686789772f28790a18532ee))
* **deps:** bump the react group in /frontend with 2 updates ([#394](https://github.com/hoivikaj/nzbdav/issues/394)) ([5ce46bc](https://github.com/hoivikaj/nzbdav/commit/5ce46bc74b0cf671a91987f92aca96c5830d4615))
* **deps:** bump the react group in /frontend with 4 updates ([#346](https://github.com/hoivikaj/nzbdav/issues/346)) ([46a8a7b](https://github.com/hoivikaj/nzbdav/commit/46a8a7bc605033c8bf64bc159f9337425044b292))
* **deps:** bump the react-router group in /frontend with 5 updates ([#345](https://github.com/hoivikaj/nzbdav/issues/345)) ([83833f4](https://github.com/hoivikaj/nzbdav/commit/83833f4e35cacc7010368a9b0935d1ed6945b58f))
* **deps:** bump the react-router group in /frontend with 5 updates ([#372](https://github.com/hoivikaj/nzbdav/issues/372)) ([27d4cea](https://github.com/hoivikaj/nzbdav/commit/27d4cea5790d92bc6f965e3dfdb3c50f9dad207a))
* **deps:** bump the tailwindcss group in /frontend with 2 updates ([#374](https://github.com/hoivikaj/nzbdav/issues/374)) ([2f1c0f8](https://github.com/hoivikaj/nzbdav/commit/2f1c0f8bf480d7d49dfdadcafba47e7e6f7ce948))
* **deps:** bump tsx from 4.20.3 to 4.21.0 in /frontend ([#326](https://github.com/hoivikaj/nzbdav/issues/326)) ([71974ec](https://github.com/hoivikaj/nzbdav/commit/71974eca1762fb72f5f9ecad181b33a8dacb413f))
* **deps:** bump typescript from 5.9.2 to 5.9.3 in /frontend ([#325](https://github.com/hoivikaj/nzbdav/issues/325)) ([1c692a6](https://github.com/hoivikaj/nzbdav/commit/1c692a66364cce5112f2c66bff55ec9ce400ba13))
* **deps:** bump vite from 6.3.5 to 7.3.1 in /frontend ([#337](https://github.com/hoivikaj/nzbdav/issues/337)) ([0f8eea6](https://github.com/hoivikaj/nzbdav/commit/0f8eea6db59d16a3aeaf4b611e8c6b8d94b77e00))
* **deps:** bump vite from 7.3.1 to 8.0.3 in /frontend in the vite group ([#375](https://github.com/hoivikaj/nzbdav/issues/375)) ([2efc0c2](https://github.com/hoivikaj/nzbdav/commit/2efc0c24ae2672afe5644a0186dbc1ebad710419))
* **deps:** bump vite-tsconfig-paths from 5.1.4 to 6.1.1 in /frontend ([#341](https://github.com/hoivikaj/nzbdav/issues/341)) ([c396ad3](https://github.com/hoivikaj/nzbdav/commit/c396ad34a826ea1cc37cf2d29e30466031eb79be))
* **deps:** bump ws from 8.18.3 to 8.19.0 in /frontend ([#342](https://github.com/hoivikaj/nzbdav/issues/342)) ([f2fa35d](https://github.com/hoivikaj/nzbdav/commit/f2fa35d86ad03c73ba5584ba2ccb3c28f25ef34d))
* **deps:** bump ws from 8.19.0 to 8.20.0 in /frontend ([#380](https://github.com/hoivikaj/nzbdav/issues/380)) ([cb42d73](https://github.com/hoivikaj/nzbdav/commit/cb42d73124d528b57addc70d542f562ca16d8496))
* **deps:** ran `npm audit fix`. ([a71cf69](https://github.com/hoivikaj/nzbdav/commit/a71cf694d9c4e0e7492ec357d68981982e148e52))
* **deps:** removed the vite-tsconfig-paths plugin. ([c2bdf1d](https://github.com/hoivikaj/nzbdav/commit/c2bdf1dd50f745f7929df623bc5bd0be5fff8887))
* downgrade unreachable Arr instance log level from Error to Debug ([#352](https://github.com/hoivikaj/nzbdav/issues/352)) ([90a03bf](https://github.com/hoivikaj/nzbdav/commit/90a03bf3e63a871b75d25ab109a6fcdd4689ffae))
* ensure `audio/flac` content-type mapping for flac files. ([5253fe3](https://github.com/hoivikaj/nzbdav/commit/5253fe3f03cbc2889928c338b2096acc7b863a52))
* fail queue items with missing nzb blobs instead of blocking queue ([#351](https://github.com/hoivikaj/nzbdav/issues/351)) ([a146d07](https://github.com/hoivikaj/nzbdav/commit/a146d07d8c62891993796b28ad358e41385dd02d))
* funnel frontend auth through middleware. ([eb71ebf](https://github.com/hoivikaj/nzbdav/commit/eb71ebf8432fc78446de1e37e4d9c5c3e81112be))
* improve error message for malformed nzbs. ([325252e](https://github.com/hoivikaj/nzbdav/commit/325252e65f910f36d0e52810ccb2fba0d1a50019))
* **nntp:** Skip failing usenet providers with circuit breaker ([#400](https://github.com/hoivikaj/nzbdav/issues/400)) ([c5fa860](https://github.com/hoivikaj/nzbdav/commit/c5fa860930a55b566a06a74006dcc777079f6716))
* **nntp:** tag provider name in connection-lock and command-error logs ([#441](https://github.com/hoivikaj/nzbdav/issues/441)) ([794948b](https://github.com/hoivikaj/nzbdav/commit/794948be293eaade7e495cb9ea88045ae33d699b))
* NZBDonkey compatibility issues with nzb category ([#316](https://github.com/hoivikaj/nzbdav/issues/316)) ([7059b10](https://github.com/hoivikaj/nzbdav/commit/7059b10c4fb79d3dda7c3745360cddbee3ef0561))
* remove 'Delete mounted files' option when clearing a failed history item. ([dfbc411](https://github.com/hoivikaj/nzbdav/commit/dfbc41148a0877cecba45bd01c97602222d1dac1))
* typo when disposing queue nzb stream. ([3e44aae](https://github.com/hoivikaj/nzbdav/commit/3e44aaebd635f6dcd9949f1d6dcd80d61985cbb0))
* update changelog link on ui leftnav-menu. ([14cd09d](https://github.com/hoivikaj/nzbdav/commit/14cd09d2a5f88438b79b46cc6b9c1200fedf0c16))
* updated opacity for disabled history actions. ([0b82f48](https://github.com/hoivikaj/nzbdav/commit/0b82f482465d0c7a81c3dca7889b57a9e0d060b2))
* updated padding on queue/history tables. ([2e83dc7](https://github.com/hoivikaj/nzbdav/commit/2e83dc74e75a27b3cba1aa5b82f5da5a0b1a8217))
* webdav range requests past content boundary return 500 instead 416 ([#384](https://github.com/hoivikaj/nzbdav/issues/384)) ([a43d5d7](https://github.com/hoivikaj/nzbdav/commit/a43d5d7e3d2de1201800dab1a38ad67b1e9d001e))

## [0.6.4](https://github.com/nzbdav-dev/nzbdav/compare/v0.6.3...v0.6.4) (2026-04-08)


### Bug Fixes

* **deps:** bump @types/node from 25.4.0 to 25.5.0 in /frontend ([#381](https://github.com/nzbdav-dev/nzbdav/issues/381)) ([680e80d](https://github.com/nzbdav-dev/nzbdav/commit/680e80df44d4a86a6c896e25c54762159fd69741))
* **deps:** bump isbot from 5.1.36 to 5.1.37 in /frontend ([#379](https://github.com/nzbdav-dev/nzbdav/issues/379)) ([b054f42](https://github.com/nzbdav-dev/nzbdav/commit/b054f42a8e2b715f94995b5e37763f8c0d9651f7))
* **deps:** Bump the dotnet group with 3 updates ([#395](https://github.com/nzbdav-dev/nzbdav/issues/395)) ([aae1e43](https://github.com/nzbdav-dev/nzbdav/commit/aae1e4367bb70f7a0a517779f453680c1e06c2bb))
* **deps:** bump the react group in /frontend with 2 updates ([#394](https://github.com/nzbdav-dev/nzbdav/issues/394)) ([5ce46bc](https://github.com/nzbdav-dev/nzbdav/commit/5ce46bc74b0cf671a91987f92aca96c5830d4615))
* **deps:** bump the react-router group in /frontend with 5 updates ([#372](https://github.com/nzbdav-dev/nzbdav/issues/372)) ([27d4cea](https://github.com/nzbdav-dev/nzbdav/commit/27d4cea5790d92bc6f965e3dfdb3c50f9dad207a))
* **deps:** bump the tailwindcss group in /frontend with 2 updates ([#374](https://github.com/nzbdav-dev/nzbdav/issues/374)) ([2f1c0f8](https://github.com/nzbdav-dev/nzbdav/commit/2f1c0f8bf480d7d49dfdadcafba47e7e6f7ce948))
* **deps:** bump vite from 7.3.1 to 8.0.3 in /frontend in the vite group ([#375](https://github.com/nzbdav-dev/nzbdav/issues/375)) ([2efc0c2](https://github.com/nzbdav-dev/nzbdav/commit/2efc0c24ae2672afe5644a0186dbc1ebad710419))
* **deps:** bump ws from 8.19.0 to 8.20.0 in /frontend ([#380](https://github.com/nzbdav-dev/nzbdav/issues/380)) ([cb42d73](https://github.com/nzbdav-dev/nzbdav/commit/cb42d73124d528b57addc70d542f562ca16d8496))

## [0.6.3](https://github.com/nzbdav-dev/nzbdav/compare/v0.6.2...v0.6.3) (2026-04-08)


### Features

* add NZB backup settings to frontend. ([55260d4](https://github.com/nzbdav-dev/nzbdav/commit/55260d41d00722b3881b4eeea5d5d07e86d5704b))
* allow exporting nzb from history table. ([7928d4b](https://github.com/nzbdav-dev/nzbdav/commit/7928d4b1fb5fc785828b4a7b211d5c62b37b6243))
* backup incoming nzbs to configured directory when enabled. ([c2b3692](https://github.com/nzbdav-dev/nzbdav/commit/c2b369229ae7ebd0bd3bfaa14c99f939d93c241e))
* index QueueItems table by category and filename. ([9116bfc](https://github.com/nzbdav-dev/nzbdav/commit/9116bfc93407dc867206f16f644f7201591ff0e1))
* organize /nzbs webdav dir by category. ([404d418](https://github.com/nzbdav-dev/nzbdav/commit/404d418a8a0a9d1465c1115b87a8506a5b9d56de))


### Bug Fixes

* remove 'Delete mounted files' option when clearing a failed history item. ([dfbc411](https://github.com/nzbdav-dev/nzbdav/commit/dfbc41148a0877cecba45bd01c97602222d1dac1))
* updated opacity for disabled history actions. ([0b82f48](https://github.com/nzbdav-dev/nzbdav/commit/0b82f482465d0c7a81c3dca7889b57a9e0d060b2))
* updated padding on queue/history tables. ([2e83dc7](https://github.com/nzbdav-dev/nzbdav/commit/2e83dc74e75a27b3cba1aa5b82f5da5a0b1a8217))
* webdav range requests past content boundary return 500 instead 416 ([#384](https://github.com/nzbdav-dev/nzbdav/issues/384)) ([a43d5d7](https://github.com/nzbdav-dev/nzbdav/commit/a43d5d7e3d2de1201800dab1a38ad67b1e9d001e))

## [0.6.2](https://github.com/nzbdav-dev/nzbdav/compare/v0.6.1...v0.6.2) (2026-03-24)


### Bug Fixes

* compatability issues with NZBDonkey ([#316](https://github.com/nzbdav-dev/nzbdav/issues/316)) ([b2d0f2a](https://github.com/nzbdav-dev/nzbdav/commit/b2d0f2a4c6b48cca688bdffb91ba1b71a3fb1b84))
* downgrade unreachable Arr instance log level from Error to Debug ([#352](https://github.com/nzbdav-dev/nzbdav/issues/352)) ([90a03bf](https://github.com/nzbdav-dev/nzbdav/commit/90a03bf3e63a871b75d25ab109a6fcdd4689ffae))
* ensure `audio/flac` content-type mapping for flac files. ([5253fe3](https://github.com/nzbdav-dev/nzbdav/commit/5253fe3f03cbc2889928c338b2096acc7b863a52))
* fail queue items with missing nzb blobs instead of blocking queue ([#351](https://github.com/nzbdav-dev/nzbdav/issues/351)) ([a146d07](https://github.com/nzbdav-dev/nzbdav/commit/a146d07d8c62891993796b28ad358e41385dd02d))
* funnel frontend auth through middleware. ([eb71ebf](https://github.com/nzbdav-dev/nzbdav/commit/eb71ebf8432fc78446de1e37e4d9c5c3e81112be))
* improve error message for malformed nzbs. ([325252e](https://github.com/nzbdav-dev/nzbdav/commit/325252e65f910f36d0e52810ccb2fba0d1a50019))
* typo when disposing queue nzb stream. ([3e44aae](https://github.com/nzbdav-dev/nzbdav/commit/3e44aaebd635f6dcd9949f1d6dcd80d61985cbb0))
* update changelog link on ui leftnav-menu. ([14cd09d](https://github.com/nzbdav-dev/nzbdav/commit/14cd09d2a5f88438b79b46cc6b9c1200fedf0c16))

## [0.6.1](https://github.com/nzbdav-dev/nzbdav/compare/v0.6.0...v0.6.1) (2026-03-11)


### Bug Fixes

* **deps:** bump @tailwindcss/vite from 4.1.11 to 4.2.1 in /frontend ([#330](https://github.com/nzbdav-dev/nzbdav/issues/330)) ([3389627](https://github.com/nzbdav-dev/nzbdav/commit/3389627c98a50370d580d614ebb0f0874d507219))
* **deps:** bump @types/express-serve-static-core ([#347](https://github.com/nzbdav-dev/nzbdav/issues/347)) ([95f8953](https://github.com/nzbdav-dev/nzbdav/commit/95f89533f1ed3f16a4c862f3e67f83d6b6ddf401))
* **deps:** bump @types/node from 20.19.10 to 25.4.0 in /frontend ([#328](https://github.com/nzbdav-dev/nzbdav/issues/328)) ([7239021](https://github.com/nzbdav-dev/nzbdav/commit/72390216d65380230fff1b0c091ec677e892a223))
* **deps:** bump bootstrap from 5.3.7 to 5.3.8 in /frontend ([#329](https://github.com/nzbdav-dev/nzbdav/issues/329)) ([1790518](https://github.com/nzbdav-dev/nzbdav/commit/17905189d379ae0d8ed0e2934d3acde7e3009785))
* **deps:** bump cross-env from 7.0.3 to 10.1.0 in /frontend ([#336](https://github.com/nzbdav-dev/nzbdav/issues/336)) ([b8d6693](https://github.com/nzbdav-dev/nzbdav/commit/b8d6693225e819127bb40063f335c8ab7a4f5ca0))
* **deps:** bump express and @types/express in /frontend ([#324](https://github.com/nzbdav-dev/nzbdav/issues/324)) ([1539ce5](https://github.com/nzbdav-dev/nzbdav/commit/1539ce5d50ac53f1ca39a65166d17ed80fb295e1))
* **deps:** bump isbot from 5.1.29 to 5.1.35 in /frontend ([#322](https://github.com/nzbdav-dev/nzbdav/issues/322)) ([2d0d069](https://github.com/nzbdav-dev/nzbdav/commit/2d0d0694ecc060134810e7c2d4bbb07aaa94a74f))
* **deps:** bump isbot from 5.1.35 to 5.1.36 in /frontend ([#349](https://github.com/nzbdav-dev/nzbdav/issues/349)) ([0619772](https://github.com/nzbdav-dev/nzbdav/commit/06197726fd2be0695027e5a7ca1ecf8c55d21586))
* **deps:** Bump Microsoft.AspNetCore.OpenApi from 10.0.1 to 10.0.4 ([#332](https://github.com/nzbdav-dev/nzbdav/issues/332)) ([7e0cfd6](https://github.com/nzbdav-dev/nzbdav/commit/7e0cfd6acada37b2b2de8961eae9d095a97f8417))
* **deps:** Bump Microsoft.EntityFrameworkCore.Design from 10.0.1 to 10.0.4 ([#334](https://github.com/nzbdav-dev/nzbdav/issues/334)) ([88fa597](https://github.com/nzbdav-dev/nzbdav/commit/88fa5976bda674e98d2bf57802fbddeb721abaaa))
* **deps:** Bump Microsoft.EntityFrameworkCore.Sqlite from 10.0.1 to 10.0.4 ([#338](https://github.com/nzbdav-dev/nzbdav/issues/338)) ([e19d72c](https://github.com/nzbdav-dev/nzbdav/commit/e19d72cd42b9ea302fc6e5dae32ea0e2652f1094))
* **deps:** bump mime-types from 3.0.1 to 3.0.2 in /frontend ([#323](https://github.com/nzbdav-dev/nzbdav/issues/323)) ([8866951](https://github.com/nzbdav-dev/nzbdav/commit/88669514ff6ff279647cd8f92f23ae9f3aa908a4))
* **deps:** bump react-dropzone from 14.3.8 to 15.0.0 in /frontend ([#348](https://github.com/nzbdav-dev/nzbdav/issues/348)) ([ab24e15](https://github.com/nzbdav-dev/nzbdav/commit/ab24e15c3b8ec3cda5c07c2943adbf1fadd1c52c))
* **deps:** bump tailwindcss from 4.1.11 to 4.2.1 in /frontend ([#335](https://github.com/nzbdav-dev/nzbdav/issues/335)) ([2a62a41](https://github.com/nzbdav-dev/nzbdav/commit/2a62a41e8b3b094f69bbb687bec775776530435b))
* **deps:** bump the react group in /frontend with 4 updates ([#346](https://github.com/nzbdav-dev/nzbdav/issues/346)) ([46a8a7b](https://github.com/nzbdav-dev/nzbdav/commit/46a8a7bc605033c8bf64bc159f9337425044b292))
* **deps:** bump the react-router group in /frontend with 5 updates ([#345](https://github.com/nzbdav-dev/nzbdav/issues/345)) ([83833f4](https://github.com/nzbdav-dev/nzbdav/commit/83833f4e35cacc7010368a9b0935d1ed6945b58f))
* **deps:** bump tsx from 4.20.3 to 4.21.0 in /frontend ([#326](https://github.com/nzbdav-dev/nzbdav/issues/326)) ([71974ec](https://github.com/nzbdav-dev/nzbdav/commit/71974eca1762fb72f5f9ecad181b33a8dacb413f))
* **deps:** bump typescript from 5.9.2 to 5.9.3 in /frontend ([#325](https://github.com/nzbdav-dev/nzbdav/issues/325)) ([1c692a6](https://github.com/nzbdav-dev/nzbdav/commit/1c692a66364cce5112f2c66bff55ec9ce400ba13))
* **deps:** bump vite from 6.3.5 to 7.3.1 in /frontend ([#337](https://github.com/nzbdav-dev/nzbdav/issues/337)) ([0f8eea6](https://github.com/nzbdav-dev/nzbdav/commit/0f8eea6db59d16a3aeaf4b611e8c6b8d94b77e00))
* **deps:** bump vite-tsconfig-paths from 5.1.4 to 6.1.1 in /frontend ([#341](https://github.com/nzbdav-dev/nzbdav/issues/341)) ([c396ad3](https://github.com/nzbdav-dev/nzbdav/commit/c396ad34a826ea1cc37cf2d29e30466031eb79be))
* **deps:** bump ws from 8.18.3 to 8.19.0 in /frontend ([#342](https://github.com/nzbdav-dev/nzbdav/issues/342)) ([f2fa35d](https://github.com/nzbdav-dev/nzbdav/commit/f2fa35d86ad03c73ba5584ba2ccb3c28f25ef34d))
